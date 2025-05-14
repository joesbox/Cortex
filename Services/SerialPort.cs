using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;

public class SerialPortService
{
    private readonly SerialPort _serialPort;

    public event EventHandler<string>? DataReceived;

    public SerialPortService(string portName, int baudRate = 921600)
    {
        _serialPort = new SerialPort(portName, baudRate);
        _serialPort.DataReceived += OnDataReceived;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        string data = _serialPort.ReadExisting();
        DataReceived?.Invoke(this, data);
    }

    public void Open() => _serialPort.Open();
    public void Close() => _serialPort.Close();
    public void Send(string text) => _serialPort.WriteLine(text);
}

