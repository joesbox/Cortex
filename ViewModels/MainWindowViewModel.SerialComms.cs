using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;

namespace Cortex.ViewModels
{
    /// <summary>
    /// Partial class containing serial port communication and connection management functionality.
    /// Handles port enumeration, connection establishment, communication handshake, and data reception.
    /// </summary>
    public partial class MainWindowViewModel
    {
        /// <summary>
        /// Loads available serial ports from the system.
        /// Preserves the current selection if it's still available.
        /// </summary>
        private void LoadSerialPorts()
        {
            string? currentSelection = SelectedSerialPort;
            var availablePorts = SerialPort.GetPortNames();
            SerialPorts = new ObservableCollection<string>(availablePorts);

            if (!string.IsNullOrWhiteSpace(currentSelection) && availablePorts.Contains(currentSelection, StringComparer.Ordinal))
            {
                SelectedSerialPort = currentSelection;
                return;
            }

            SelectedSerialPort = SerialPorts.FirstOrDefault();
        }

        /// <summary>
        /// Refreshes the list of available serial ports.
        /// </summary>
        [RelayCommand]
        private void RefreshSerialPorts()
        {
            LoadSerialPorts();
        }

        /// <summary>
        /// Establishes a connection to the PDM via the specified serial port.
        /// Initializes the port service and begins communication handshake.
        /// </summary>
        [RelayCommand]
        private void Connect(string? selectedPort)
        {
            string? portName = string.IsNullOrWhiteSpace(selectedPort)
                ? SelectedSerialPort
                : selectedPort;

            if (string.IsNullOrWhiteSpace(portName))
            {
                AddLog("Select a PDM connection before connecting.");
                return;
            }

            _portService = new SerialPortService(portName);
            _portService.DataUpdated += _portService_DataUpdated;
            _portService.ConfigurationSaved += ConfigSaved;
            _portService.ConfigurationSaveCompleted += ConfigSaveCompleted;
            refreshStaticData = true;
            IsConnected = _portService.Open();

            if (IsConnected)
            {
                Debug.WriteLine($"Connecting to PDM on {portName}.");
                AddLog("Connecting to PDM...");
                _portService.InitComms();
                return;
            }

            Debug.WriteLine(_portService.LastError ?? $"Failed to open serial port {portName}.");
            AddLog("Couldn't connect to the PDM. Check the cable and try again.");
            _portService.DataUpdated -= _portService_DataUpdated;
            _portService.ConfigurationSaved -= ConfigSaved;
            _portService.ConfigurationSaveCompleted -= ConfigSaveCompleted;
            _portService = null;
        }

        /// <summary>
        /// Manages the communication establishment and live data polling.
        /// Called periodically via the comms timer to maintain connection state.
        /// </summary>
        private void HandleComms()
        {
            if (IsConnected)
            {
                if (!CommsEstablished)
                {
                    if (_portService != null)
                    {
                        CommsEstablished = _portService.InitComms();
                        if (CommsEstablished)
                        {
                            AddLog("PDM connected.");
                        }
                    }
                }
                else if (_portService != null)
                {
                    _portService.EnsureLiveRequestPolling();

                    if (!IsConnectedPdmRegistered)
                    {
                        return;
                    }

                    if (DateTime.UtcNow >= _nextCellularHealthPollUtc)
                    {
                        ScheduleCellularHealthPoll(immediate: false);
                        _ = PollCellularHealthStatusAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Disconnects from the PDM and cleans up all communication resources.
        /// Resets connection and communication state, stopping any pending operations.
        /// </summary>
        [RelayCommand]
        private void Disconnect()
        {
            if (_portService != null)
            {
                _portService.DataUpdated -= _portService_DataUpdated;
                _portService.ConfigurationSaved -= ConfigSaved;
                _portService.ConfigurationSaveCompleted -= ConfigSaveCompleted;
                _portService.Close();
                _portService = null;
            }

            IsSendingConfig = false;
            CommsEstablished = false;
            IsConnected = false;
            _hasReceivedLiveData = false;
            ResetLiveChartHistory();
            ResetLiveChartSeries();
            AddLog("Disconnected from PDM.");
        }

        /// <summary>
        /// Event handler for when the port service receives updated data from the PDM.
        /// Queues the data for UI update on the next UI thread cycle in a thread-safe manner.
        /// </summary>
        private void _portService_DataUpdated(DataStructures obj)
        {
            _hasReceivedLiveData = true;
            lock (_pendingDataLock)
            {
                _pendingLiveData = obj;
                _hasPendingData = true;
            }
        }
    }
}

