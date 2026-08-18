using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Cortex.Models
{
    [Serializable]
    public partial class CellularParameters : ObservableObject
    {
        private bool _updatingOpenRemoteTopic;

        [ObservableProperty]
        public byte _ConfigVersion = Constants.CELLULAR_CONFIG_VERSION;

        [ObservableProperty]
        public byte _Protocol = Constants.CELLULAR_PROTOCOL_MQTT;

        [ObservableProperty]
        public bool _UseTLS;

        [ObservableProperty]
        public string? _APN;

        [ObservableProperty]
        public string? _APNUser;

        [ObservableProperty]
        public string? _APNPassword;

        [ObservableProperty]
        public string? _OpenRemoteHost = Constants.CELLULAR_DEFAULT_OPENREMOTE_HOST;

        [ObservableProperty]
        public ushort _OpenRemotePort = Constants.CELLULAR_DEFAULT_MQTT_PORT;

        [ObservableProperty]
        public string? _ClientID;

        [ObservableProperty]
        public string? _OpenRemoteRealm;

        [ObservableProperty]
        public string? _OpenRemoteAssetId;

        [ObservableProperty]
        public string? _OpenRemoteAssetName;

        [ObservableProperty]
        public string? _MQTTUsername;

        [ObservableProperty]
        public string? _MQTTPassword;

        [ObservableProperty]
        public string? _PublishTopic;

        [ObservableProperty]
        public string? _SubscribeTopic;

        [ObservableProperty]
        public ushort _KeepAliveSeconds = Constants.CELLULAR_DEFAULT_KEEPALIVE_SECONDS;

        [ObservableProperty]
        public uint _PublishIntervalMs = Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS;

        [ObservableProperty]
        public uint _TelemetryUploadMask = Constants.TELEMETRY_UPLOAD_DEFAULT_MASK;

        public uint PublishIntervalSeconds
        {
            get => Math.Max(Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS / 1000, PublishIntervalMs / 1000);
            set => PublishIntervalMs = Math.Max(Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS / 1000, value) * 1000;
        }

        public bool UploadAnalogue1Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE1_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE1_VALUE, value); }
        public bool UploadAnalogue2Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE2_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE2_VALUE, value); }
        public bool UploadAnalogue3Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE3_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE3_VALUE, value); }
        public bool UploadAnalogue4Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE4_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE4_VALUE, value); }
        public bool UploadAnalogue5Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE5_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE5_VALUE, value); }
        public bool UploadAnalogue6Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE6_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE6_VALUE, value); }
        public bool UploadAnalogue7Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE7_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE7_VALUE, value); }
        public bool UploadAnalogue8Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE8_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_ANALOGUE8_VALUE, value); }
        public bool UploadDigital1Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL1_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL1_VALUE, value); }
        public bool UploadDigital2Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL2_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL2_VALUE, value); }
        public bool UploadDigital3Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL3_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL3_VALUE, value); }
        public bool UploadDigital4Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL4_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL4_VALUE, value); }
        public bool UploadDigital5Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL5_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL5_VALUE, value); }
        public bool UploadDigital6Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL6_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL6_VALUE, value); }
        public bool UploadDigital7Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL7_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL7_VALUE, value); }
        public bool UploadDigital8Value { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL8_VALUE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_DIGITAL8_VALUE, value); }
        public bool UploadAnalogueInputs
        {
            get => UploadAnalogue1Value || UploadAnalogue2Value || UploadAnalogue3Value || UploadAnalogue4Value || UploadAnalogue5Value || UploadAnalogue6Value || UploadAnalogue7Value || UploadAnalogue8Value;
            set
            {
                SetTelemetryFlags(Constants.TELEMETRY_UPLOAD_ANALOGUE1_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE2_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE3_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE4_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE5_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE6_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE7_VALUE | Constants.TELEMETRY_UPLOAD_ANALOGUE8_VALUE, value);
                OnPropertyChanged(nameof(UploadAnalogue1Value));
                OnPropertyChanged(nameof(UploadAnalogue2Value));
                OnPropertyChanged(nameof(UploadAnalogue3Value));
                OnPropertyChanged(nameof(UploadAnalogue4Value));
                OnPropertyChanged(nameof(UploadAnalogue5Value));
                OnPropertyChanged(nameof(UploadAnalogue6Value));
                OnPropertyChanged(nameof(UploadAnalogue7Value));
                OnPropertyChanged(nameof(UploadAnalogue8Value));
            }
        }

        public bool UploadDigitalInputs
        {
            get => UploadDigital1Value || UploadDigital2Value || UploadDigital3Value || UploadDigital4Value || UploadDigital5Value || UploadDigital6Value || UploadDigital7Value || UploadDigital8Value;
            set
            {
                SetTelemetryFlags(Constants.TELEMETRY_UPLOAD_DIGITAL1_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL2_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL3_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL4_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL5_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL6_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL7_VALUE | Constants.TELEMETRY_UPLOAD_DIGITAL8_VALUE, value);
                OnPropertyChanged(nameof(UploadDigital1Value));
                OnPropertyChanged(nameof(UploadDigital2Value));
                OnPropertyChanged(nameof(UploadDigital3Value));
                OnPropertyChanged(nameof(UploadDigital4Value));
                OnPropertyChanged(nameof(UploadDigital5Value));
                OnPropertyChanged(nameof(UploadDigital6Value));
                OnPropertyChanged(nameof(UploadDigital7Value));
                OnPropertyChanged(nameof(UploadDigital8Value));
            }
        }

        public bool UploadGpsData { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_GPS_SPEED) || GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_LOCATION); set => SetTelemetryFlags(Constants.TELEMETRY_UPLOAD_GPS_SPEED | Constants.TELEMETRY_UPLOAD_LOCATION, value); }
        public bool UploadImuData { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_IMU_DATA); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_IMU_DATA, value); }
        public bool UploadChannelCurrents { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_CHANNEL_CURRENTS); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_CHANNEL_CURRENTS, value); }
        public bool UploadSystemCurrent { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_SYSTEM_CURRENT); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_SYSTEM_CURRENT, value); }
        public bool UploadSystemTemperature { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_SYSTEM_TEMPERATURE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_SYSTEM_TEMPERATURE, value); }
        public bool UploadSystemVoltage { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_SYSTEM_VOLTAGE); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_SYSTEM_VOLTAGE, value); }
        public bool UploadUptime { get => GetTelemetryFlag(Constants.TELEMETRY_UPLOAD_UPTIME); set => SetTelemetryFlag(Constants.TELEMETRY_UPLOAD_UPTIME, value); }

        public void EnsurePublishTopicFromOpenRemoteFields()
        {
            UpdatePublishTopicFromOpenRemoteFields();
            EnsureOpenRemoteMqttUsernameFromRealm();
        }

        public void PreserveOpenRemoteFieldsFrom(CellularParameters source)
        {
            if (source == null)
            {
                return;
            }

            _updatingOpenRemoteTopic = true;
            if (string.IsNullOrWhiteSpace(OpenRemoteRealm) && !string.IsNullOrWhiteSpace(source.OpenRemoteRealm))
            {
                OpenRemoteRealm = source.OpenRemoteRealm;
            }

            if (string.IsNullOrWhiteSpace(OpenRemoteAssetId) && !string.IsNullOrWhiteSpace(source.OpenRemoteAssetId))
            {
                OpenRemoteAssetId = source.OpenRemoteAssetId;
            }

            _updatingOpenRemoteTopic = false;

            UpdatePublishTopicFromOpenRemoteFields();
            EnsureOpenRemoteMqttUsernameFromRealm();
        }

        partial void OnPublishIntervalMsChanged(uint value)
        {
            uint minimumPublishIntervalMs = Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS;
            if (value < minimumPublishIntervalMs)
            {
                PublishIntervalMs = minimumPublishIntervalMs;
                return;
            }

            NotifyTelemetryIntervalProperties();
        }

        partial void OnTelemetryUploadMaskChanged(uint value)
        {
            OnPropertyChanged(nameof(UploadAnalogue1Value));
            OnPropertyChanged(nameof(UploadAnalogue2Value));
            OnPropertyChanged(nameof(UploadAnalogue3Value));
            OnPropertyChanged(nameof(UploadAnalogue4Value));
            OnPropertyChanged(nameof(UploadAnalogue5Value));
            OnPropertyChanged(nameof(UploadAnalogue6Value));
            OnPropertyChanged(nameof(UploadAnalogue7Value));
            OnPropertyChanged(nameof(UploadAnalogue8Value));
            OnPropertyChanged(nameof(UploadDigital1Value));
            OnPropertyChanged(nameof(UploadDigital2Value));
            OnPropertyChanged(nameof(UploadDigital3Value));
            OnPropertyChanged(nameof(UploadDigital4Value));
            OnPropertyChanged(nameof(UploadDigital5Value));
            OnPropertyChanged(nameof(UploadDigital6Value));
            OnPropertyChanged(nameof(UploadDigital7Value));
            OnPropertyChanged(nameof(UploadDigital8Value));
            OnPropertyChanged(nameof(UploadAnalogueInputs));
            OnPropertyChanged(nameof(UploadDigitalInputs));
            OnPropertyChanged(nameof(UploadGpsData));
            OnPropertyChanged(nameof(UploadImuData));
            OnPropertyChanged(nameof(UploadChannelCurrents));
            OnPropertyChanged(nameof(UploadSystemCurrent));
            OnPropertyChanged(nameof(UploadSystemTemperature));
            OnPropertyChanged(nameof(UploadSystemVoltage));
            OnPropertyChanged(nameof(UploadUptime));
        }

        partial void OnUseTLSChanged(bool value)
        {
            if (value && OpenRemotePort == Constants.CELLULAR_DEFAULT_MQTT_PORT)
            {
                OpenRemotePort = Constants.CELLULAR_DEFAULT_MQTT_TLS_PORT;
            }
            else if (!value && OpenRemotePort == Constants.CELLULAR_DEFAULT_MQTT_TLS_PORT)
            {
                OpenRemotePort = Constants.CELLULAR_DEFAULT_MQTT_PORT;
            }
        }

        partial void OnClientIDChanged(string? value) => UpdatePublishTopicFromOpenRemoteFields();

        partial void OnOpenRemoteRealmChanged(string? value) => UpdatePublishTopicFromOpenRemoteFields();

        partial void OnOpenRemoteAssetIdChanged(string? value) => UpdatePublishTopicFromOpenRemoteFields();

        partial void OnPublishTopicChanged(string? value)
        {
            if (_updatingOpenRemoteTopic)
            {
                return;
            }

            TryPopulateOpenRemoteFieldsFromTopic(value);
        }

        private void UpdatePublishTopicFromOpenRemoteFields()
        {
            if (_updatingOpenRemoteTopic)
            {
                return;
            }

            string? realm = OpenRemoteRealm?.Trim();
            string? clientId = ClientID?.Trim();
            string attributeName = Constants.OPENREMOTE_COMPATIBILITY_ATTRIBUTE;
            string? assetId = OpenRemoteAssetId?.Trim();
            if (string.IsNullOrWhiteSpace(realm) ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(assetId))
            {
                return;
            }

            _updatingOpenRemoteTopic = true;
            PublishTopic = $"{realm}/{clientId}/writeattributevalue/{attributeName}/{assetId}";
            SubscribeTopic = $"{realm}/{clientId}/attributevalue/{attributeName}/{assetId}";
            _updatingOpenRemoteTopic = false;
        }

        private void EnsureOpenRemoteMqttUsernameFromRealm()
        {
            string? realm = OpenRemoteRealm?.Trim();
            string? clientId = ClientID?.Trim();
            if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            MQTTUsername = $"{realm}:ps-{clientId}";
        }

        private uint GetConfiguredTelemetryUploadMask()
        {
            return TelemetryUploadMask != 0 ? TelemetryUploadMask : Constants.TELEMETRY_UPLOAD_DEFAULT_MASK;
        }

        private void NotifyTelemetryIntervalProperties()
        {
            OnPropertyChanged(nameof(PublishIntervalSeconds));
        }

        private bool GetTelemetryFlag(uint flag) => (TelemetryUploadMask & flag) != 0;

        private void SetTelemetryFlag(uint flag, bool enabled) => SetTelemetryFlags(flag, enabled);

        private void SetTelemetryFlags(uint flags, bool enabled)
        {
            uint newMask = enabled ? (TelemetryUploadMask | flags) : (TelemetryUploadMask & ~flags);
            if (newMask != TelemetryUploadMask)
            {
                TelemetryUploadMask = newMask;
            }
        }

        private void TryPopulateOpenRemoteFieldsFromTopic(string? topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            string[] parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 5 || !parts[2].Equals("writeattributevalue", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _updatingOpenRemoteTopic = true;
            OpenRemoteRealm = parts[0];
            OpenRemoteAssetId = parts[4];
            _updatingOpenRemoteTopic = false;
        }
    }
}
