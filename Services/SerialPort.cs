using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Xml;
using static Cortex.Models.OutputChannel;


public class SerialPortService
{
    public event Action<DataStructures>? DataUpdated;

    private SerialPort _serialPort;

    private DataStructures dataStructures;

    private DataStructures settingsData;

    private byte[] dataBytes;
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

    /// <summary>
    /// Setting index: 0 = channel, 1 = analogue input, 2 = system
    /// </summary>
    private int settingIndex;

    /// <summary>
    /// Parameter index within setting: e.g. for channel data, 0 = type, 1 = current limit high, etc.
    /// </summary>
    private int parameterIndex;

    private int channelIndex;
    private int analogueIndex;


    public bool UpdateStaticData = false;

    private List<byte> _sendBuffer = new List<byte>();

    public SerialPortService(string portName, int baudRate = 9600)
    {
        _serialPort = new SerialPort(portName, baudRate);
        _serialPort.Parity = Parity.Even;
        _serialPort.DataBits = 8;
        _serialPort.StopBits = StopBits.Two;
        _serialPort.RtsEnable = true;
        _serialPort.DtrEnable = true;
        _serialPort.DataReceived += OnDataReceived;
        receivedDataBuffer = new List<byte>();
        dataStructures = new DataStructures();
        settingsData = new DataStructures();
        dataBytes = [];
    }

    public void UpdateSettingsData(DataStructures newSettingsData)
    {
        this.settingsData = newSettingsData;
    }

    /// <summary>
    /// Process incoming data from the serial port
    /// </summary>
    /// <returns>True if a full data packet received and processed</returns>
    private bool processData()
    {        
        bool retVal = false;
        byte[] checkSumArray = new byte[4];
        // Reset trailer flag
        foundTrailer1 = foundTrailer2 = false;
        dataBytes = receivedDataBuffer.ToArray();
        receivedDataBuffer.Clear();
        int checkSum = 0;

        checkSum = dataBytes.Take(dataBytes.Length - 4).Sum(b => (int)b);

        Array.Copy(dataBytes, dataBytes.Length - 4, checkSumArray, 0, 4);

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
                        if (Constants.NUM_OUTPUT_CHANNELS == dataBytes[3])
                        {
                            int dataIndex = 4; // Start after header, command, and number of channels

                            var reader = new ByteReader(dataBytes, dataIndex);

                            for (int i = 0; i < dataBytes[3]; i++)
                            {
                                dataStructures.ChannelsLiveData[i].ChanType = (ChannelType)reader.ReadByte();
                                dataStructures.ChannelsLiveData[i].CurrentLimitHigh = reader.ReadSingle();
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
                                dataStructures.ChannelsLiveData[i].InrushDelay = reader.ReadSingle() / 1000.0F;
                                dataStructures.ChannelsLiveData[i].Name = reader.ReadChars(3);
                            }

                            int numAnalogueChannels = reader.ReadByte();

                            Debug.WriteLine($"Num analogue channels: {numAnalogueChannels}");


                            for (int i = 0; i < numAnalogueChannels; i++) // Analogue input channel data coming in
                            {
                                dataStructures.AnalogueInputsStaticData[i].PullUpEnable = reader.ReadByte() != 0;
                                dataStructures.AnalogueInputsStaticData[i].PullDownEnable = reader.ReadByte() != 0;
                                dataStructures.AnalogueInputsStaticData[i].IsDigital = reader.ReadByte() != 0;
                                dataStructures.AnalogueInputsStaticData[i].OnThreshold = reader.ReadSingle();
                                dataStructures.AnalogueInputsStaticData[i].OffThreshold = reader.ReadSingle();
                                dataStructures.AnalogueInputsStaticData[i].InputScaleLow = reader.ReadSingle();
                                dataStructures.AnalogueInputsStaticData[i].InputScaleHigh = reader.ReadSingle();
                                dataStructures.AnalogueInputsStaticData[i].PwmLowValue = reader.ReadByte();
                                dataStructures.AnalogueInputsStaticData[i].PwmHighValue = reader.ReadByte();
                            }

                            dataStructures.SystemParams.SystemTemperature = reader.ReadInt32();
                            dataStructures.SystemParams.CANResEnabled = reader.ReadByte();
                            dataStructures.SystemParams.VBatt = reader.ReadSingle();
                            dataStructures.SystemParams.SystemCurrent = reader.ReadSingle();
                            dataStructures.SystemParams.SystemCurrentLimit = reader.ReadByte();
                            dataStructures.SystemParams.ErrorFlags = reader.ReadUInt16();
                            dataStructures.SystemParams.ChannelDataCANID = reader.ReadUInt16();
                            dataStructures.SystemParams.SystemDataCANID = reader.ReadUInt16();
                            dataStructures.SystemParams.ConfigDataCANID = reader.ReadUInt16();
                            dataStructures.SystemParams.IMUWakeWindow = reader.ReadUInt32();
                            dataStructures.SystemParams.SpeedUnitPref = reader.ReadByte();
                            dataStructures.SystemParams.DistanceUnitPref = reader.ReadByte();
                            dataStructures.SystemParams.AllowData = reader.ReadByte();
                            dataStructures.SystemParams.AllowGPS = reader.ReadByte();
                            dataStructures.SystemParams.BattSOC = reader.ReadInt32();
                            dataStructures.SystemParams.BattSOH = reader.ReadInt32();

                            if (UpdateStaticData)
                            {
                                // Copy live data to static data
                                dataStructures.ChannelsStaticData = dataStructures.ChannelsLiveData;
                                dataStructures.SystemParamsStaticData = dataStructures.SystemParams;
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
                    case 1: // Current limit high
                        if (dataStructures.ChannelsLiveData[channelIndex].CurrentLimitHigh != settingsData.ChannelsStaticData[channelIndex].CurrentLimitHigh)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].CurrentLimitHigh);
                            foreach (byte b in floatBytes) AddData(b, true);
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
                            foreach (byte b in floatBytes) AddData(b, true);
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
                            foreach (byte b in floatBytes) AddData(b, true);
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
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].InrushDelay * 1000.0F);
                            foreach (byte b in floatBytes) AddData(b, true);
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
                }

                break;
            case 1: // Analogue input data
                break;
            case 2: // System data
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
            Debug.Write("Setting, parameter, channel: ");
            Debug.Write(settingIndex);
            Debug.Write(", ");
            Debug.Write(parameterIndex);
            Debug.Write(", ");
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
            //Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " Serial data received");
            while (_serialPort.BytesToRead > 0 && _serialPort.IsOpen)
            {
                byte readByte = (byte)_serialPort.ReadByte();

                receivedDataBuffer.Add(readByte);

                if (readByte == Constants.SERIAL_TRAILER1 || foundTrailer1)
                {
                    foundTrailer1 = true;
                    if (readByte == Constants.SERIAL_TRAILER2 || foundTrailer2)
                    {
                        foundTrailer2 = true;
                        switch (lastCommandSent)
                        {
                            case Constants.COMMAND_ID_REQUEST:
                                // Trailer found and we've read all the bytes. Process data
                                if (_serialPort.BytesToRead == 0)
                                {
                                    UpdateStaticData = true;
                                    processData();
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
                                receivedDataBuffer.Clear();
                                SendCommand(Constants.COMMAND_ID_REQUEST);
                            }
                            break;
                        case Constants.COMMAND_ID_NEWCONFIG:
                        case Constants.COMMAND_ID_SKIP:
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
                                parameterIndex = channelIndex = analogueIndex = settingIndex = 0;
                            }

                            if (saveToEEPROM)
                            {
                                saveToEEPROM = false;
                                SendCommand(Constants.COMMAND_ID_SAVECHANGES);
                            }
                            break;

                        case Constants.COMMAND_ID_SAVECHANGES:
                            Debug.WriteLine("Configuration saved to EEPROM.");
                            break;
                    }
                }
                else if (readByte == Constants.COMMAND_ID_CHECKSUM_FAIL)
                {                    
                    switch (lastCommandSent)
                    {                        
                        case Constants.COMMAND_ID_NEWCONFIG:
                            //SendConfig();
                            break;
                        case Constants.COMMAND_ID_SAVECHANGES:

                            break;
                    }
                }
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
}

