using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using static Cortex.Models.OutputChannel;


public class SerialPortService
{
    public event Action<DataStructures>? DataUpdated;

    private SerialPort _serialPort;

    private DataStructures dataStructures;

    private byte[] dataBytes;
    private List<byte> receivedDataBuffer;
    private bool foundTrailer1;
    private bool foundTrailer2;
    private UInt32 pdmCheckSum;

    public bool foundECU;

    public bool UpdateStaticData = false;

    public SerialPortService(string portName, int baudRate = 921600)
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
        dataBytes = [];
    }

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
                            int dataIndex = 4; // Start after header, command, and num_channels

                            for (int i = 0; i < dataBytes[3]; i++)
                            {
                                // Parse each channel's data based on the order sent by microcontroller

                                // Channel Type (1 byte)
                                dataStructures.ChannelsLiveData[i].ChanType = (ChannelType)dataBytes[dataIndex];
                                dataIndex += 1;

                                // CurrentLimitHigh (4 bytes float)
                                dataStructures.ChannelsLiveData[i].CurrentLimitHigh = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;

                                // CurrentSensePin (1 byte)
                                dataStructures.ChannelsLiveData[i].CurrentSensePin = dataBytes[dataIndex];
                                dataIndex += 1;

                                // CurrentThresholdHigh (4 bytes float)
                                dataStructures.ChannelsLiveData[i].CurrentThresholdHigh = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;

                                // CurrentThresholdLow (4 bytes float)
                                dataStructures.ChannelsLiveData[i].CurrentThresholdLow = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;

                                // CurrentValue (4 bytes float)
                                dataStructures.ChannelsLiveData[i].CurrentValue = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;

                                // Enabled (1 byte)
                                dataStructures.ChannelsLiveData[i].Enabled = dataBytes[dataIndex];
                                dataIndex += 1;

                                // ErrorFlags (1 byte)
                                dataStructures.ChannelsLiveData[i].ErrorFlags = dataBytes[dataIndex];
                                dataIndex += 1;

                                // GroupNumber (1 byte)
                                dataStructures.ChannelsLiveData[i].GroupNumber = dataBytes[dataIndex];
                                dataIndex += 1;

                                // InputControlPin (1 byte)
                                dataStructures.ChannelsLiveData[i].InputControlPin = dataBytes[dataIndex];
                                dataIndex += 1;

                                // MultiChannel (1 byte)
                                dataStructures.ChannelsLiveData[i].MultiChannel = dataBytes[dataIndex];
                                dataIndex += 1;

                                // Retry (1 byte)
                                dataStructures.ChannelsLiveData[i].Retry = dataBytes[dataIndex];
                                dataIndex += 1;

                                // RetryCount (1 byte)
                                dataStructures.ChannelsLiveData[i].RetryCount = dataBytes[dataIndex];
                                dataIndex += 1;

                                // RetryDelay (4 bytes float)
                                dataStructures.ChannelsLiveData[i].RetryDelay = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;
                            }

                            int numAnalogueChannels = dataBytes[dataIndex];
                            dataIndex += 1;

                            Debug.WriteLine($"Num analogue channels: {numAnalogueChannels}");
                            Debug.WriteLine($"Data index: {dataIndex}");


                            for (int i = 0; i < numAnalogueChannels; i++) // Analogue input channel data coming in
                            {
                                dataStructures.AnalogueInputsStaticData[i].PullUpEnable = Convert.ToBoolean(dataBytes[dataIndex]);
                                dataIndex += 1;
                                dataStructures.AnalogueInputsStaticData[i].PullDownEnable = Convert.ToBoolean(dataBytes[dataIndex]);
                                dataIndex += 1;
                                dataStructures.AnalogueInputsStaticData[i].IsDigital = Convert.ToBoolean(dataBytes[dataIndex]);
                                dataIndex += 1;
                                dataStructures.AnalogueInputsStaticData[i].OnThreshold = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;
                                dataStructures.AnalogueInputsStaticData[i].OffThreshold = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;
                                dataStructures.AnalogueInputsStaticData[i].InputScaleLow = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;
                                dataStructures.AnalogueInputsStaticData[i].InputScaleHigh = BitConverter.ToSingle(dataBytes, dataIndex);
                                dataIndex += 4;
                                dataStructures.AnalogueInputsStaticData[i].PwmLowValue = dataBytes[dataIndex];
                                dataIndex += 1;
                                dataStructures.AnalogueInputsStaticData[i].PwmHighValue = dataBytes[dataIndex];
                                dataIndex += 1;
                            }

                            if (UpdateStaticData)
                            {
                                // Copy live data to static data
                                dataStructures.ChannelsStaticData = dataStructures.ChannelsLiveData;
                                UpdateStaticData = false;
                            }
                        }
                        break;
                }
            }

            DataUpdated?.Invoke(dataStructures);

            float limit = BitConverter.ToSingle(dataBytes, 4);
            Debug.WriteLine(header);
            Debug.WriteLine(dataBytes[2]);
            Debug.WriteLine(dataBytes[3]);
            Debug.WriteLine(limit);
        }

        SendCommand(Constants.COMMAND_ID_REQUEST);

        return retVal;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort.IsOpen)
        {
            while (_serialPort.BytesToRead > 0)
            {
                byte readByte = (byte)_serialPort.ReadByte();

                receivedDataBuffer.Add(readByte);

                Debug.WriteLine(readByte);

                if (readByte == Constants.SERIAL_TRAILER1 || foundTrailer1)
                {
                    foundTrailer1 = true;
                    if (readByte == Constants.SERIAL_TRAILER2 || foundTrailer2)
                    {
                        foundTrailer2 = true;
                        // Trailer found and we've read all the bytes. Process data
                        if (_serialPort.BytesToRead == 0)
                        {
                            UpdateStaticData = true;
                            processData();
                        }
                    }
                }

                if (readByte == Constants.COMMAND_ID_CONFIM)
                {
                    foundECU = true;
                    receivedDataBuffer.Clear();
                    SendCommand(Constants.COMMAND_ID_REQUEST);
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

    private void SendCommand(char commandId)
    {
        if (_serialPort.IsOpen)
        {
            byte[] data = new byte[1];
            data[0] = (byte)commandId;
            _serialPort.Write(data, 0, data.Length);
            Debug.WriteLine(data[0].ToString());
        }
    }
}

