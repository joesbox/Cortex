using Cortex.Models;
using Cortex.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Timers;
using static Cortex.Models.OutputChannel;


public class SerialPortService
{
    public event Action<DataStructures>? DataUpdated;

    private SerialPort _serialPort;

    private DataStructures dataStructures;

    private DataStructures settingsData;
        
    private List<byte> receivedDataBuffer;
    private bool foundTrailer1;
    private bool foundTrailer2;
    private UInt32 pdmCheckSum;
    private char lastCommandSent;
    private bool sendingConfig;
    private bool saveToEEPROM;
    public bool foundECU;
    private int totalBytesSent;
    private int checkSumSend;

    private bool overridding;

    /// <summary>
    /// Setting index: 0 = channel, 1 = analogue input, 2 = system, 3 = digital
    /// </summary>
    private int settingIndex;

    /// <summary>
    /// Parameter index within setting: e.g. for channel data, 0 = type, 1 = current limit high, etc.
    /// </summary>
    private int parameterIndex;

    private int channelIndex;
    private int analogueIndex;
    private int digitalIndex;


    public bool UpdateStaticData = false;

    private List<byte> _sendBuffer = new List<byte>();

    // Timer to process received packets off the serial thread at a 100ms interval
    private readonly Timer _processTimer;
    private readonly object _bufferLock = new object();
    private volatile bool _packetReady = false;

    private byte[] dataBytes = new byte[4096]; 
    private byte[] checkSumArray = new byte[4];
    private int dataLength;

    public SerialPortService(string portName, int baudRate = 921600)
    {
        _serialPort = new SerialPort(portName, baudRate);        
        _serialPort.DataBits = 8;        
        _serialPort.RtsEnable = true;
        _serialPort.DtrEnable = true;
        _serialPort.DataReceived += OnDataReceived;
        receivedDataBuffer = new List<byte>();
        dataStructures = new DataStructures();
        settingsData = new DataStructures();
        dataBytes = Array.Empty<byte>();
        overridding = false;

        // create and start the processing timer (100ms)
        _processTimer = new Timer(50);
        _processTimer.AutoReset = true;
        _processTimer.Elapsed += (s, e) =>
        {
            // if a full packet has been flagged, process it on timer thread
            if (!_packetReady || overridding)
            {
                return;
            }

            // ensure only one thread processes the buffer
            lock (_bufferLock)
            {
                // clear flag before processing to avoid re-entrancy
                _packetReady = false;
                // processData will internally copy and clear the buffer
                try
                {
                    processData();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in processData: {ex.Message}");
                }
            }

        };
        _processTimer.Start();
    }

    public void UpdateSettingsData(DataStructures newSettingsData)
    {
        settingsData = newSettingsData;
    }

    /// <summary>
    /// Process incoming data from the serial port
    /// </summary>
    /// <returns>True if a full data packet received and processed</returns>
    private bool processData()
    {
        bool retVal = false;
        //byte[] checkSumArray = new byte[4];
        // Reset trailer flag
        foundTrailer1 = foundTrailer2 = false;

        // copy buffer to local array under lock
        lock (_bufferLock)
        {
            dataLength = receivedDataBuffer.Count;

            if (dataLength > dataBytes.Length)
            {
                dataBytes = new byte[dataLength * 2]; // Only resize when needed
            }

            receivedDataBuffer.CopyTo(dataBytes, 0);
            receivedDataBuffer.Clear();
        }

        if (dataBytes == null || dataBytes.Length < 6)
        {
            return false;
        }

        int checkSum = 0;
        for (int i = 0; i < dataLength - 4; i++)
        {
            checkSum += dataBytes[i];
        }

        // Copy checksum bytes to reusable array
        Array.Copy(dataBytes, dataLength - 4, checkSumArray, 0, 4);
        pdmCheckSum = BitConverter.ToUInt32(checkSumArray, 0);

        // First two bytes are the header
        uint header = BitConverter.ToUInt16(dataBytes, 0);

        // Checksums match. Continue.
        if (checkSum == pdmCheckSum)
        {
            // Header check
            if (header == 0x1984)
            {
                switch (dataBytes[2])
                {
                    // Request response for channel and system data
                    case 114:
                        // Data 3 contains number of channels
                        if (dataBytes[3] == Constants.NUM_OUTPUT_CHANNELS)
                        {
                            int dataIndex = 4; // Start after header, command, and number of channels

                            var reader = new ByteReader(dataBytes, dataIndex);

                            for (int i = 0; i < dataBytes[3]; i++)
                            {
                                dataStructures.ChannelsLiveData[i].ChanType = (ChannelType)reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].Override = reader.ReadByte() != 0;
                                dataStructures.ChannelsLiveData[i].CurrentSensePin = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].CurrentThresholdHigh = reader.ReadSingle();
                                dataStructures.ChannelsLiveData[i].CurrentThresholdLow = reader.ReadSingle();
                                dataStructures.ChannelsLiveData[i].CurrentValue = reader.ReadSingle();
                                dataStructures.ChannelsLiveData[i].Enabled = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].ErrorFlags = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].GroupNumber = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].InputControlPin = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].MultiChannel = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].RetryCount = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].InrushDelay = reader.ReadInt32() / 1000.0F;
                                dataStructures.ChannelsLiveData[i].Name = reader.ReadChars(3);
                                dataStructures.ChannelsLiveData[i].RunOn = reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].RunOnTime = reader.ReadInt32() / 1000;
                            }

                            int numAnalogueChannels = reader.ReadByte();


                            for (int i = 0; i < numAnalogueChannels; i++) // Analogue input channel data coming in
                            {
                                dataStructures.AnalogueInputsLiveData[i].PullUpEnable = reader.ReadByte() != 0;
                                dataStructures.AnalogueInputsLiveData[i].PullDownEnable = reader.ReadByte() != 0;
                                dataStructures.AnalogueInputsLiveData[i].IsDigital = reader.ReadByte() != 0;
                                dataStructures.AnalogueInputsLiveData[i].OnThreshold = reader.ReadSingle();
                                dataStructures.AnalogueInputsLiveData[i].OffThreshold = reader.ReadSingle();
                                dataStructures.AnalogueInputsLiveData[i].InputScaleLow = reader.ReadSingle();
                                dataStructures.AnalogueInputsLiveData[i].InputScaleHigh = reader.ReadSingle();
                                dataStructures.AnalogueInputsLiveData[i].PwmLowValue = reader.ReadByte();
                                dataStructures.AnalogueInputsLiveData[i].PwmHighValue = reader.ReadByte();
                            }

                            dataStructures.SystemParams.SystemTemperature = reader.ReadInt32();
                            dataStructures.SystemParams.CANResEnabled = reader.ReadByte() != 0;
                            dataStructures.SystemParams.VBatt = (float)Math.Round(reader.ReadSingle(), 1);
                            dataStructures.SystemParams.SystemCurrent = reader.ReadSingle();
                            dataStructures.SystemParams.SystemCurrentLimit = reader.ReadByte();
                            dataStructures.SystemParams.ErrorFlags = reader.ReadUInt16();
                            dataStructures.SystemParams.ChannelDataCANID = reader.ReadUInt16();
                            dataStructures.SystemParams.SystemDataCANID = reader.ReadUInt16();
                            dataStructures.SystemParams.ConfigDataCANID = reader.ReadUInt16();
                            dataStructures.SystemParams.IMUWakeWindow = reader.ReadUInt32();
                            dataStructures.SystemParams.SpeedUnitPref = reader.ReadByte() != 0;
                            dataStructures.SystemParams.DistanceUnitPref = reader.ReadByte() != 0;
                            dataStructures.SystemParams.AllowData = reader.ReadByte() != 0;
                            dataStructures.SystemParams.AllowGPS = reader.ReadByte() != 0;
                            dataStructures.SystemParams.BattSOC = reader.ReadInt32();
                            dataStructures.SystemParams.BattSOH = reader.ReadInt32();

                            if (UpdateStaticData)
                            {
                                // Copy live data to static data
                                dataStructures.ChannelsStaticData = dataStructures.ChannelsLiveData;
                                dataStructures.SystemParamsStaticData = dataStructures.SystemParams;
                                dataStructures.AnalogueInputsStaticData = dataStructures.AnalogueInputsLiveData;
                                UpdateStaticData = false;
                            }
                        }
                        break;
                }
            }

            DataUpdated?.Invoke(dataStructures);

            float limit = BitConverter.ToSingle(dataBytes, 4);

        }
        if (!sendingConfig)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
        }


        return retVal;
    }

    private bool SendConfig()
    {
        bool retVal = false;
        totalBytesSent = 0;
        checkSumSend = 0;
        _sendBuffer.Clear();
        bool configChanged = false;

        AddData(Constants.SERIAL_HEADER1, true);
        AddData(Constants.SERIAL_HEADER2, true);

        switch (settingIndex)
        {
            case 0: // Channel data
                switch (parameterIndex)
                {
                    case 0: // Channel type
                        if (dataStructures.ChannelsLiveData[channelIndex].ChanType != settingsData.ChannelsStaticData[channelIndex].ChanType)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)settingsData.ChannelsStaticData[channelIndex].ChanType, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 1: // Override
                        if (dataStructures.ChannelsLiveData[channelIndex].Override != settingsData.ChannelsStaticData[channelIndex].Override)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].Override ? (byte)1 : (byte)0, true);
                        }
                        break;
                    case 2: // Current threshold high
                        if (dataStructures.ChannelsLiveData[channelIndex].CurrentThresholdHigh != settingsData.ChannelsStaticData[channelIndex].CurrentThresholdHigh)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].CurrentThresholdHigh);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 3: // Current threshold low
                        if (dataStructures.ChannelsLiveData[channelIndex].CurrentThresholdLow != settingsData.ChannelsStaticData[channelIndex].CurrentThresholdLow)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].CurrentThresholdLow);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 4: // Enabled
                        if (dataStructures.ChannelsLiveData[channelIndex].Enabled != settingsData.ChannelsStaticData[channelIndex].Enabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].Enabled, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 5: // Group number
                        if (dataStructures.ChannelsLiveData[channelIndex].GroupNumber != settingsData.ChannelsStaticData[channelIndex].GroupNumber)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].GroupNumber, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 6: // Input control pin
                        if (dataStructures.ChannelsLiveData[channelIndex].InputControlPin != settingsData.ChannelsStaticData[channelIndex].InputControlPin)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].InputControlPin, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 7: // Multi channel
                        if (dataStructures.ChannelsLiveData[channelIndex].MultiChannel != settingsData.ChannelsStaticData[channelIndex].MultiChannel)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MultiChannel, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 8: // Retry count
                        if (dataStructures.ChannelsLiveData[channelIndex].RetryCount != settingsData.ChannelsStaticData[channelIndex].RetryCount)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].RetryCount, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 9: // Inrush delay
                        if (dataStructures.ChannelsLiveData[channelIndex].InrushDelay != settingsData.ChannelsStaticData[channelIndex].InrushDelay)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes((int)settingsData.ChannelsStaticData[channelIndex].InrushDelay * 1000);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 10: // Name
                        if (!(dataStructures.ChannelsLiveData[channelIndex].Name ?? Array.Empty<char>())
                              .SequenceEqual(settingsData.ChannelsStaticData[channelIndex].Name ?? Array.Empty<char>()))
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            foreach (char c in settingsData.ChannelsStaticData[channelIndex].Name)
                            {
                                AddData((byte)c, true);
                            }
                            AddData(0, true); // Padding
                        }
                        break;
                    case 11: // Run on
                        if (dataStructures.ChannelsLiveData[channelIndex].RunOn != settingsData.ChannelsStaticData[channelIndex].RunOn)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].RunOn, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;

                    case 12: // Run on time
                        if (dataStructures.ChannelsLiveData[channelIndex].RunOnTime != settingsData.ChannelsStaticData[channelIndex].RunOnTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);

                            Debug.WriteLine("Setting run on time to " + settingsData.ChannelsStaticData[channelIndex].RunOnTime + " seconds.");
                            byte[] floatBytes = BitConverter.GetBytes((int)settingsData.ChannelsStaticData[channelIndex].RunOnTime * 1000);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                }

                break;
            case 1: // Analogue input data
                switch (parameterIndex)
                {
                    case 0: // Pull-up enable
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].PullUpEnable != settingsData.AnalogueInputsStaticData[analogueIndex].PullUpEnable)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].PullUpEnable ? (byte)1 : (byte)0, true);
                        }
                        break;
                    case 1: // Pull-down enable
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].PullDownEnable != settingsData.AnalogueInputsStaticData[analogueIndex].PullDownEnable)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].PullDownEnable ? (byte)1 : (byte)0, true);
                        }
                        break;
                    case 2: // Is digital
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].IsDigital != settingsData.AnalogueInputsStaticData[analogueIndex].IsDigital)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].IsDigital ? (byte)1 : (byte)0, true);
                        }
                        break;

                    case 3: // Is threshold
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].IsThreshold != settingsData.AnalogueInputsStaticData[analogueIndex].IsThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].IsThreshold ? (byte)1 : (byte)0, true);
                        }
                        break;
                    case 4: // On threshold
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].OnThreshold != settingsData.AnalogueInputsStaticData[analogueIndex].OnThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].OnThreshold);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 5: // Off threshold
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].OffThreshold != settingsData.AnalogueInputsStaticData[analogueIndex].OffThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].OffThreshold);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 6: // Input scale low
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].InputScaleLow != settingsData.AnalogueInputsStaticData[analogueIndex].InputScaleLow)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].InputScaleLow);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 7: // Input scale high
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].InputScaleHigh != settingsData.AnalogueInputsStaticData[analogueIndex].InputScaleHigh)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].InputScaleHigh);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 8: // PWM low value
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].PwmLowValue != settingsData.AnalogueInputsStaticData[analogueIndex].PwmLowValue)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].PwmLowValue, true);
                        }
                        break;
                    case 9: // PWM high value
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].PwmHighValue != settingsData.AnalogueInputsStaticData[analogueIndex].PwmHighValue)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].PwmHighValue, true);
                        }
                        break;


                }
                break;
            case 2: // System data
                switch (parameterIndex)
                {
                    case 0: // CAN Resistor enabled
                        if (dataStructures.SystemParamsStaticData.CANResEnabled != settingsData.SystemParamsStaticData.CANResEnabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(settingsData.SystemParamsStaticData.CANResEnabled ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 1: // Channel data CAN ID
                        if (dataStructures.SystemParamsStaticData.ChannelDataCANID != settingsData.SystemParamsStaticData.ChannelDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.ChannelDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 2: // System data CAN ID
                        if (dataStructures.SystemParamsStaticData.SystemDataCANID != settingsData.SystemParamsStaticData.SystemDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.SystemDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 3: // Config data CAN ID
                        if (dataStructures.SystemParamsStaticData.ConfigDataCANID != settingsData.SystemParamsStaticData.ConfigDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.ConfigDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 4: // IMU wake window
                        if (dataStructures.SystemParamsStaticData.IMUWakeWindow != settingsData.SystemParamsStaticData.IMUWakeWindow)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.IMUWakeWindow);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 5: // Speed unit preference
                        if (dataStructures.SystemParamsStaticData.SpeedUnitPref != settingsData.SystemParamsStaticData.SpeedUnitPref)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(settingsData.SystemParamsStaticData.SpeedUnitPref ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 6: // Distance unit preference
                        if (dataStructures.SystemParamsStaticData.DistanceUnitPref != settingsData.SystemParamsStaticData.DistanceUnitPref)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(settingsData.SystemParamsStaticData.DistanceUnitPref ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 7: // Allow GSM data
                        if (dataStructures.SystemParamsStaticData.AllowData != settingsData.SystemParamsStaticData.AllowData)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(settingsData.SystemParamsStaticData.AllowData ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 8: // Allow GPS data
                        if (dataStructures.SystemParamsStaticData.AllowGPS != settingsData.SystemParamsStaticData.AllowGPS)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(settingsData.SystemParamsStaticData.AllowGPS ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                }
                break;

            case 3: // Digital inputs
                switch (parameterIndex)
                {
                    case 0: // Digital input active high
                        if (dataStructures.DigitalInputsLiveData[digitalIndex].IsActiveHigh != settingsData.DigitalInputsStaticData[digitalIndex].IsActiveHigh)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)digitalIndex, true);
                            AddData(settingsData.DigitalInputsStaticData[digitalIndex].IsActiveHigh ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                }
                break;
        }

        AddData(Constants.SERIAL_TRAILER1, true);
        AddData(Constants.SERIAL_TRAILER2, true);
        AddData((byte)(checkSumSend & 0xFF), false);
        AddData((byte)((checkSumSend >> 8) & 0xFF), false);
        AddData((byte)((checkSumSend >> 16) & 0xFF), false);
        AddData((byte)((checkSumSend >> 24) & 0xFF), false);

        if (configChanged)
        {
            switch (settingIndex)
                {
                case 0:
                    Debug.WriteLine("Sending channel " + channelIndex + " parameter " + parameterIndex);
                    break;
                case 1:
                    Debug.WriteLine("Sending analogue input " + analogueIndex + " parameter " + parameterIndex);
                    break;
                case 2:
                    Debug.WriteLine("Sending system parameter " + parameterIndex);
                    break;
                case 3:
                    Debug.WriteLine("Sending digital input " + digitalIndex + " parameter " + parameterIndex);
                    break;
            }
          
            Debug.WriteLine(channelIndex);
            if (_serialPort.IsOpen && _serialPort != null)
            {
                SendCommand(Constants.COMMAND_ID_NEWCONFIG);
                _serialPort.Write(_sendBuffer.ToArray(), 0, _sendBuffer.Count);

                Debug.Write("Wrote ");
                Debug.Write(_sendBuffer.Count);
                Debug.WriteLine(" bytes.");
                _sendBuffer.Clear();
            }
        }
        else
        {
            _sendBuffer.Clear();
            SendCommand(Constants.COMMAND_ID_SKIP);
        }

        return retVal;
    }

    private void AddData(byte data, bool addToCheck)
    {
        _sendBuffer.Add(data);
        totalBytesSent++;
        if (addToCheck)
        {
            checkSumSend += data;
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort.IsOpen)
        {
            try
            {                
                while (_serialPort.BytesToRead > 0 && _serialPort.IsOpen)
                {
                    byte readByte = (byte)_serialPort.ReadByte();

                    lock (_bufferLock)
                    {
                        receivedDataBuffer.Add(readByte);
                    }

                    if (readByte == Constants.SERIAL_TRAILER1 || foundTrailer1)
                    {
                        foundTrailer1 = true;
                        if (readByte == Constants.SERIAL_TRAILER2 || foundTrailer2)
                        {
                            foundTrailer2 = true;
                            switch (lastCommandSent)
                            {
                                case Constants.COMMAND_ID_REQUEST:
                                    // Trailer found and we've read all the bytes. Flag packet ready for processing by timer
                                    if (_serialPort.BytesToRead == 0)
                                    {
                                        UpdateStaticData = true;
                                        _packetReady = true;
                                    }
                                    break;
                            }

                        }
                    }

                    if (readByte == Constants.COMMAND_ID_CONFIM)
                    {
                        switch (lastCommandSent)
                        {
                            case Constants.COMMAND_ID_BEGIN:
                                if (!sendingConfig)
                                {
                                    foundECU = true;
                                    lock (_bufferLock)
                                    {
                                        receivedDataBuffer.Clear();
                                    }
                                    SendCommand(Constants.COMMAND_ID_REQUEST);
                                }
                                break;
                            case Constants.COMMAND_ID_NEWCONFIG:
                            case Constants.COMMAND_ID_SKIP:
                                if (!overridding)
                                {
                                    switch (settingIndex)
                                    {
                                        case 0: // Channel data
                                            parameterIndex++;
                                            if (parameterIndex > Constants.NUMBER_CHANNEL_PARAMETERS)
                                            {
                                                parameterIndex = 0;
                                                channelIndex++;
                                                if (channelIndex >= Constants.NUM_OUTPUT_CHANNELS)
                                                {
                                                    channelIndex = 0;
                                                    settingIndex++;
                                                }
                                            }
                                            break;
                                        case 1: // Analogue input data
                                            parameterIndex++;
                                            if (parameterIndex > Constants.NUMBER_ANALOGUE_PARAMETERS)
                                            {
                                                parameterIndex = 0;
                                                analogueIndex++;
                                                if (analogueIndex >= Constants.NUM_ANALOGUE_INPUTS)
                                                {
                                                    analogueIndex = 0;
                                                    settingIndex++;
                                                }
                                            }
                                            break;
                                        case 2: // System data
                                            parameterIndex++;
                                            if (parameterIndex > Constants.NUMBER_SYSTEM_PARAMETERS)
                                            {
                                                parameterIndex = 0;
                                                settingIndex++;
                                            }
                                            break;
                                        case 3: // Digital inputs
                                            parameterIndex++;
                                            if (parameterIndex > Constants.NUMBER_DIGITAL_INPUT_PARAMETERS)
                                            {
                                                digitalIndex++;
                                                sendingConfig = false;
                                                saveToEEPROM = true;
                                                // That's it. Finished sending config.
                                            }
                                            break;
                                    }                                    

                                    if (sendingConfig)
                                    {
                                        SendConfig();
                                    }
                                    else
                                    {
                                        parameterIndex = channelIndex = analogueIndex = settingIndex = digitalIndex = 0;
                                    }

                                    if (saveToEEPROM)
                                    {
                                        saveToEEPROM = false;
                                        SendCommand(Constants.COMMAND_ID_SAVECHANGES);

                                    }
                                }
                                else
                                {
                                    overridding = false;
                                    SendCommand(Constants.COMMAND_ID_REQUEST);
                                }
                                break;

                            case Constants.COMMAND_ID_SAVECHANGES:
                                lock (_bufferLock)
                                {
                                    receivedDataBuffer.Clear();
                                }
                                SendCommand(Constants.COMMAND_ID_REQUEST);
                                Debug.WriteLine("Configuration saved to EEPROM.");
                                LoggingService.AddLog("PDM updated.");
                                break;
                        }
                    }
                    else if (readByte == Constants.COMMAND_ID_CHECKSUM_FAIL)
                    {
                        switch (lastCommandSent)
                        {
                            case Constants.COMMAND_ID_NEWCONFIG:
                                LoggingService.AddLog("PDM reported checksum failure for config data. Try again.");
                                break;
                            case Constants.COMMAND_ID_SAVECHANGES:
                                LoggingService.AddLog("PDM reported checksum failure when saving changes. Try again.");

                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Do nothing. Disconnect was probably hit
            }
        }
    }

    public bool InitComms()
    {
        bool retVal = foundECU;
        if (_serialPort is not null)
        {
            if (_serialPort.IsOpen)
            {
                if (!foundECU)
                {
                    try
                    {
                        SendCommand(Constants.COMMAND_ID_BEGIN);

                        if (_serialPort.BytesToRead > 0)
                        {
                            Debug.WriteLine("Data in buffer");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error sending request: {ex.Message}");
                    }
                }
            }
        }
        return retVal;
    }

    public bool Open()
    {
        bool retVal = false;

        try
        {
            _serialPort.Open();

            if (_serialPort.IsOpen)
            {
                retVal = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error connecting: {ex.Message}");
        }
        return retVal;
    }
    public void Close() => _serialPort.Close();
    public void Send(string text) => _serialPort.WriteLine(text);

    public void StartSendConfig()
    {
        sendingConfig = true;
        settingIndex = 0;
        channelIndex = 0;
        analogueIndex = 0;
        parameterIndex = 0;
        SendConfig();
        LoggingService.AddLog("Sending config to PDM...");
    }

    private void SendCommand(char commandId)
    {
        if (_serialPort.IsOpen)
        {
            byte[] data = new byte[1];
            data[0] = (byte)commandId;
            _serialPort.Write(data, 0, data.Length);
            lastCommandSent = commandId;
        }
    }

    /// <summary>
    /// Send override command immediately for a specific channel
    /// </summary>
    public void SendOverrideCommand(int channelIndex, bool overrideState)
    {
        if (!_serialPort.IsOpen) return;

        overridding = true;

        _sendBuffer.Clear();
        checkSumSend = 0;

        // Build packet
        AddData(Constants.SERIAL_HEADER1, true);
        AddData(Constants.SERIAL_HEADER2, true);
        AddData(0, true);  // settingIndex = 0 (Channel data)
        AddData(1, true);  // parameterIndex = 1 (Override)
        AddData((byte)channelIndex, true);
        AddData(overrideState ? (byte)1 : (byte)0, true);
        AddData(Constants.SERIAL_TRAILER1, true);
        AddData(Constants.SERIAL_TRAILER2, true);
        AddData((byte)(checkSumSend & 0xFF), false);
        AddData((byte)((checkSumSend >> 8) & 0xFF), false);
        AddData((byte)((checkSumSend >> 16) & 0xFF), false);
        AddData((byte)((checkSumSend >> 24) & 0xFF), false);

        // Send the command
        SendCommand(Constants.COMMAND_ID_NEWCONFIG);
        _serialPort.Write(_sendBuffer.ToArray(), 0, _sendBuffer.Count);
        _sendBuffer.Clear();

    }
}

