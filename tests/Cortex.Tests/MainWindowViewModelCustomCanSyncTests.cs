using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Cortex.Tests;

public class MainWindowViewModelCustomCanSyncTests
{
    [Fact]
    public void UpdateLiveDataInPlace_CopiesCustomCanDataIntoLiveAndSettingsViews()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DataStructures source = new();
            source.CustomCanMessagesLiveData[0].Index = 0;
            source.CustomCanMessagesLiveData[0].Id = 0x18FF50E5;
            source.CustomCanMessagesLiveData[0].TimeoutMs = 1500;
            source.CustomCanMessagesLiveData[0].Dlc = 8;
            source.CustomCanMessagesLiveData[0].Flags = 0x03;
            source.CustomCanMessagesLiveData[0].Status = (byte)CustomCanStatus.Valid;

            source.CustomCanSignalsLiveData[0].Index = 0;
            source.CustomCanSignalsLiveData[0].Name = "Boost";
            source.CustomCanSignalsLiveData[0].MessageName = "BoostMsg";
            source.CustomCanSignalsLiveData[0].MessageIndex = 0;
            source.CustomCanSignalsLiveData[0].StartBit = 16;
            source.CustomCanSignalsLiveData[0].BitLength = 12;
            source.CustomCanSignalsLiveData[0].Flags = 0x01;
            source.CustomCanSignalsLiveData[0].Factor = 0.5f;
            source.CustomCanSignalsLiveData[0].Offset = -10.0f;
            source.CustomCanSignalsLiveData[0].RuntimeValue = 24.5f;
            source.CustomCanSignalsLiveData[0].Status = (byte)CustomCanStatus.Valid;

            source.CustomCanMessagesStaticData[0].Index = 0;
            source.CustomCanMessagesStaticData[0].Id = 0x18FF50E5;
            source.CustomCanMessagesStaticData[0].TimeoutMs = 1500;
            source.CustomCanMessagesStaticData[0].Dlc = 8;
            source.CustomCanMessagesStaticData[0].Flags = 0x03;
            source.CustomCanMessagesStaticData[0].Status = (byte)CustomCanStatus.Valid;

            source.CustomCanSignalsStaticData[0].Index = 0;
            source.CustomCanSignalsStaticData[0].Name = "Boost";
            source.CustomCanSignalsStaticData[0].MessageName = "BoostMsg";
            source.CustomCanSignalsStaticData[0].MessageIndex = 0;
            source.CustomCanSignalsStaticData[0].StartBit = 16;
            source.CustomCanSignalsStaticData[0].BitLength = 12;
            source.CustomCanSignalsStaticData[0].Flags = 0x01;
            source.CustomCanSignalsStaticData[0].Factor = 0.5f;
            source.CustomCanSignalsStaticData[0].Offset = -10.0f;
            source.CustomCanSignalsStaticData[0].RuntimeValue = 24.5f;
            source.CustomCanSignalsStaticData[0].Status = (byte)CustomCanStatus.Valid;

            InvokeUpdateLiveDataInPlace(viewModel, source);

            CustomCanMessage liveMessage = viewModel.LiveDataView.CustomCanMessagesLiveData[0];
            CustomCanSignal liveSignal = viewModel.LiveDataView.CustomCanSignalsLiveData[0];
            CustomCanMessage staticMessage = viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
            CustomCanSignal staticSignal = viewModel.SettingsDataView.CustomCanSignalsStaticData[0];

            Assert.Equal(source.CustomCanMessagesLiveData[0].Id, liveMessage.Id);
            Assert.Equal(source.CustomCanMessagesLiveData[0].TimeoutMs, liveMessage.TimeoutMs);
            Assert.Equal(source.CustomCanMessagesLiveData[0].Flags, liveMessage.Flags);
            Assert.Equal(source.CustomCanSignalsLiveData[0].Name, liveSignal.Name);
            Assert.Equal(source.CustomCanSignalsLiveData[0].MessageName, liveSignal.MessageName);
            Assert.Equal(source.CustomCanSignalsLiveData[0].RuntimeValue, liveSignal.RuntimeValue);
            Assert.Equal(source.CustomCanSignalsLiveData[0].Status, liveSignal.Status);

            Assert.Equal(source.CustomCanMessagesStaticData[0].Id, staticMessage.Id);
            Assert.Equal(source.CustomCanMessagesStaticData[0].TimeoutMs, staticMessage.TimeoutMs);
            Assert.Equal(source.CustomCanMessagesStaticData[0].Flags, staticMessage.Flags);
            Assert.Equal(source.CustomCanSignalsStaticData[0].Name, staticSignal.Name);
            Assert.Equal(source.CustomCanSignalsStaticData[0].MessageName, staticSignal.MessageName);
            Assert.Equal(source.CustomCanSignalsStaticData[0].RuntimeValue, staticSignal.RuntimeValue);
            Assert.Equal(source.CustomCanSignalsStaticData[0].Status, staticSignal.Status);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void UpdateLiveDataInPlace_RefreshesStoredCustomCanSignalProjection()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DataStructures source = new();
            source.CustomCanMessagesStaticData[0].Index = 0;
            source.CustomCanMessagesStaticData[0].Id = 0x18FF50E5;
            source.CustomCanMessagesStaticData[0].TimeoutMs = 1500;
            source.CustomCanMessagesStaticData[0].Dlc = 8;
            source.CustomCanMessagesStaticData[0].Flags = 0x03;
            source.CustomCanMessagesStaticData[0].Status = (byte)CustomCanStatus.Valid;

            source.CustomCanSignalsStaticData[0].Index = 0;
            source.CustomCanSignalsStaticData[0].Name = "Boost";
            source.CustomCanSignalsStaticData[0].MessageIndex = 0;
            source.CustomCanSignalsStaticData[0].Flags = 0x01;
            source.CustomCanSignalsStaticData[0].Status = (byte)CustomCanStatus.Valid;

            InvokeUpdateLiveDataInPlace(viewModel, source);

            List<CustomCanSignal> storedSignals = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Single(storedSignals);
            Assert.Equal(0, storedSignals[0].Index);
            Assert.Equal("0x18FF50E5", storedSignals[0].MessageIdHex);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ControllerCustomCanSignals_ReturnsOnlyEnabledSignals_WithMessageIdHex()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.SettingsDataView.CustomCanMessagesStaticData[0].Id = 0x18FF50E5;
            viewModel.SettingsDataView.CustomCanMessagesStaticData[0].Flags = 0x03;

            viewModel.SettingsDataView.CustomCanSignalsStaticData[0].Index = 0;
            viewModel.SettingsDataView.CustomCanSignalsStaticData[0].MessageIndex = 0;
            viewModel.SettingsDataView.CustomCanSignalsStaticData[0].Flags = 0x01;

            viewModel.SettingsDataView.CustomCanSignalsStaticData[1].Index = 1;
            viewModel.SettingsDataView.CustomCanSignalsStaticData[1].MessageIndex = 0;
            viewModel.SettingsDataView.CustomCanSignalsStaticData[1].Flags = 0x00;

            InvokeNonPublicMethod(viewModel, "RefreshCustomCanBindings");

            List<CustomCanSignal> storedSignals = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Single(storedSignals);
            Assert.Equal(0, storedSignals[0].Index);
            Assert.Equal("0x18FF50E5", storedSignals[0].MessageIdHex);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void FindOrAllocateSignalSlot_ReturnsFirstDisabledSignalSlot()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.SettingsDataView.CustomCanSignalsStaticData[0].Flags = 0x01;
            viewModel.SettingsDataView.CustomCanSignalsStaticData[1].Flags = 0x00;

            int slotIndex = InvokePrivateIntMethod(viewModel, "FindOrAllocateSignalSlot");

            Assert.Equal(1, slotIndex);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ApplyPendingCustomCanSnapshotIfAvailable_UpdatesSettingsBeforeSignalSlotAllocation()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DataStructures pending = new();
            pending.CustomCanSignalsStaticData[0].Flags = 0x01;
            pending.CustomCanSignalsStaticData[0].MessageIndex = 0;

            SetPrivateField(viewModel, "_pendingLiveData", pending);
            SetPrivateField(viewModel, "_hasPendingData", true);

            InvokeNonPublicMethod(viewModel, "ApplyPendingCustomCanSnapshotIfAvailable");

            int slotIndex = InvokePrivateIntMethod(viewModel, "FindOrAllocateSignalSlot");
            Assert.Equal(1, slotIndex);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ParseDbc_ReadsMinMaxUnits_AndInfersRangeWhenMissing()
    {
        const string dbc = """
            BO_ 100 EngineData: 8 Vector__XXX
             SG_ Pressure : 0|16@1+ (0.1,0) [0|100] "bar" Vector__XXX
             SG_ Speed : 16|8@1+ (2,-10) "rpm" Vector__XXX
            """;

        (List<DbcCanMessageDefinition> messages, List<DbcCanSignalDefinition> signals) = InvokeParseDbc(dbc);

        Assert.Single(messages);
        Assert.Equal(2, signals.Count);

        DbcCanSignalDefinition pressure = signals.Single(signal => signal.Name == "Pressure");
        Assert.Equal(0f, pressure.MinValue);
        Assert.Equal(100f, pressure.MaxValue);
        Assert.Equal("bar", pressure.Units);

        DbcCanSignalDefinition speed = signals.Single(signal => signal.Name == "Speed");
        Assert.Equal(-10f, speed.MinValue);
        Assert.Equal(500f, speed.MaxValue);
        Assert.Equal("rpm", speed.Units);
    }

    [Fact]
    public void UpdateLiveDataInPlace_ReconcilesLocalCustomCanMetadataForEnabledSignals()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.SettingsDataView.CustomCanSignalsMetadata.Add(new CustomCanSignalMetadata
            {
                SignalIndex = 0,
                MessageIndex = 0,
                StartBit = 8,
                BitLength = 8,
                IsMotorola = false,
                IsSigned = false,
                Factor = 1.0f,
                Offset = 0.0f,
                Name = "CoolantTemp",
                MinValue = -40f,
                MaxValue = 125f,
                Units = "C",
            });

            DataStructures source = new();
            source.CustomCanMessagesStaticData[0].Index = 0;
            source.CustomCanMessagesStaticData[0].Id = 0x321;
            source.CustomCanMessagesStaticData[0].Dlc = 8;
            source.CustomCanMessagesStaticData[0].Flags = 0x01;

            source.CustomCanSignalsStaticData[0].Index = 0;
            source.CustomCanSignalsStaticData[0].MessageIndex = 0;
            source.CustomCanSignalsStaticData[0].StartBit = 8;
            source.CustomCanSignalsStaticData[0].BitLength = 8;
            source.CustomCanSignalsStaticData[0].Flags = 0x01;
            source.CustomCanSignalsStaticData[0].Factor = 1.0f;
            source.CustomCanSignalsStaticData[0].Offset = 0.0f;

            InvokeUpdateLiveDataInPlace(viewModel, source);

            CustomCanSignal reconciled = viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
            Assert.Equal("CoolantTemp", reconciled.Name);
            Assert.True(reconciled.MinValue is null or -40f);
            Assert.True(reconciled.MaxValue is null or 125f);
            Assert.True(string.IsNullOrWhiteSpace(reconciled.Units) || reconciled.Units == "C");
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void UpdateLiveDataInPlace_PreservesLocalMetadataAcrossDisabledThenEnabledSnapshots()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.SettingsDataView.CustomCanSignalsMetadata.Add(new CustomCanSignalMetadata
            {
                SignalIndex = 0,
                MessageIndex = 0,
                StartBit = 8,
                BitLength = 8,
                IsMotorola = false,
                IsSigned = false,
                Factor = 1.0f,
                Offset = 0.0f,
                Name = "CoolantTemp",
                MinValue = -40f,
                MaxValue = 125f,
                Units = "C",
            });

            DataStructures disabledSnapshot = new();
            disabledSnapshot.CustomCanMessagesStaticData[0].Index = 0;
            disabledSnapshot.CustomCanMessagesStaticData[0].Id = 0x321;
            disabledSnapshot.CustomCanMessagesStaticData[0].Dlc = 8;
            disabledSnapshot.CustomCanMessagesStaticData[0].Flags = 0x00;
            disabledSnapshot.CustomCanSignalsStaticData[0].Index = 0;
            disabledSnapshot.CustomCanSignalsStaticData[0].MessageIndex = 0;
            disabledSnapshot.CustomCanSignalsStaticData[0].StartBit = 8;
            disabledSnapshot.CustomCanSignalsStaticData[0].BitLength = 8;
            disabledSnapshot.CustomCanSignalsStaticData[0].Flags = 0x00;
            disabledSnapshot.CustomCanSignalsStaticData[0].Factor = 1.0f;
            disabledSnapshot.CustomCanSignalsStaticData[0].Offset = 0.0f;

            InvokeUpdateLiveDataInPlace(viewModel, disabledSnapshot);

            DataStructures enabledSnapshot = new();
            enabledSnapshot.CustomCanMessagesStaticData[0].Index = 0;
            enabledSnapshot.CustomCanMessagesStaticData[0].Id = 0x321;
            enabledSnapshot.CustomCanMessagesStaticData[0].Dlc = 8;
            enabledSnapshot.CustomCanMessagesStaticData[0].Flags = 0x01;
            enabledSnapshot.CustomCanSignalsStaticData[0].Index = 0;
            enabledSnapshot.CustomCanSignalsStaticData[0].MessageIndex = 0;
            enabledSnapshot.CustomCanSignalsStaticData[0].StartBit = 8;
            enabledSnapshot.CustomCanSignalsStaticData[0].BitLength = 8;
            enabledSnapshot.CustomCanSignalsStaticData[0].Flags = 0x01;
            enabledSnapshot.CustomCanSignalsStaticData[0].Factor = 1.0f;
            enabledSnapshot.CustomCanSignalsStaticData[0].Offset = 0.0f;

            InvokeUpdateLiveDataInPlace(viewModel, enabledSnapshot);

            CustomCanSignal reconciled = viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
            Assert.Equal("CoolantTemp", reconciled.Name);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void UpdateLiveDataInPlace_ReconcilesMetadataBySignalSlotWhenIdentityDiffers()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.SettingsDataView.CustomCanSignalsMetadata.Add(new CustomCanSignalMetadata
            {
                SignalIndex = 0,
                MessageIndex = 0,
                StartBit = 8,
                BitLength = 8,
                IsMotorola = false,
                IsSigned = false,
                Factor = 1.0f,
                Offset = 0.0f,
                Name = "CoolantTemp",
                MinValue = -40f,
                MaxValue = 125f,
                Units = "C",
            });

            DataStructures source = new();
            source.CustomCanMessagesStaticData[0].Index = 0;
            source.CustomCanMessagesStaticData[0].Id = 0x321;
            source.CustomCanMessagesStaticData[0].Dlc = 8;
            source.CustomCanMessagesStaticData[0].Flags = 0x01;

            source.CustomCanSignalsStaticData[0].Index = 0;
            source.CustomCanSignalsStaticData[0].MessageIndex = 0;
            source.CustomCanSignalsStaticData[0].StartBit = 8;
            source.CustomCanSignalsStaticData[0].BitLength = 8;
            source.CustomCanSignalsStaticData[0].Flags = 0x01;
            source.CustomCanSignalsStaticData[0].Factor = 1.0001f;
            source.CustomCanSignalsStaticData[0].Offset = 0.0f;

            InvokeUpdateLiveDataInPlace(viewModel, source);

            CustomCanSignal reconciled = viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
            Assert.Equal("CoolantTemp", reconciled.Name);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void UpdateLiveDataInPlace_PreservesLocalNameAndLimits_WhenControllerSendsPlaceholderName()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.SettingsDataView.CustomCanSignalsMetadata.Add(new CustomCanSignalMetadata
            {
                SignalIndex = 0,
                MessageIndex = 0,
                StartBit = 8,
                BitLength = 8,
                IsMotorola = false,
                IsSigned = false,
                Factor = 1.0f,
                Offset = 0.0f,
                Name = "CoolantTemp",
                MinValue = -40f,
                MaxValue = 125f,
                Units = "C",
            });

            DataStructures source = new();
            source.CustomCanMessagesStaticData[0].Index = 0;
            source.CustomCanMessagesStaticData[0].Id = 0x321;
            source.CustomCanMessagesStaticData[0].Dlc = 8;
            source.CustomCanMessagesStaticData[0].Flags = 0x01;

            source.CustomCanSignalsStaticData[0].Index = 0;
            source.CustomCanSignalsStaticData[0].Name = "SignalA";
            source.CustomCanSignalsStaticData[0].MessageIndex = 0;
            source.CustomCanSignalsStaticData[0].StartBit = 8;
            source.CustomCanSignalsStaticData[0].BitLength = 8;
            source.CustomCanSignalsStaticData[0].Flags = 0x01;
            source.CustomCanSignalsStaticData[0].Factor = 1.0f;
            source.CustomCanSignalsStaticData[0].Offset = 0.0f;

            InvokeUpdateLiveDataInPlace(viewModel, source);

            CustomCanSignal reconciled = viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
            Assert.Equal("CoolantTemp", reconciled.Name);
            Assert.True(reconciled.MinValue is null or -40f);
            Assert.True(reconciled.MaxValue is null or 125f);
            Assert.True(string.IsNullOrWhiteSpace(reconciled.Units) || reconciled.Units == "C");
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportSelectedDbcSignalToController_ImportsAllSelectedSignals()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition first = new()
            {
                Name = "SignalA",
                MessageId = 0x321,
                MessageName = "MsgA",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            DbcCanSignalDefinition second = new()
            {
                Name = "SignalB",
                MessageId = 0x322,
                MessageName = "MsgB",
                StartBit = 8,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };

            viewModel.ImportedDbcSignals.Add(first);
            viewModel.ImportedDbcSignals.Add(second);
            viewModel.SetSelectedImportedDbcSignals([first, second]);

            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            List<CustomCanSignal> storedSignals = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Equal(2, storedSignals.Count);
            Assert.Contains(storedSignals, signal => signal.Name == "SignalA");
            Assert.Contains(storedSignals, signal => signal.Name == "SignalB");
            Assert.Equal("Imported 2 DBC signals.", viewModel.CustomCanStatusMessage);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportSelectedDbcSignalToController_ReimportingSameSignal_UpdatesExistingSlot()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition signal = new()
            {
                Name = "SignalA",
                MessageId = 0x321,
                MessageName = "MsgA",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };

            viewModel.ImportedDbcSignals.Add(signal);
            viewModel.SetSelectedImportedDbcSignals([signal]);

            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");
            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            List<CustomCanSignal> storedSignals = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Single(storedSignals);
            Assert.Equal("SignalA", storedSignals[0].Name);
            Assert.Equal(0x321u, viewModel.SettingsDataView.CustomCanMessagesStaticData[storedSignals[0].MessageIndex].Id);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportSelectedDbcSignalToController_DeduplicatesEquivalentSelectedSignals()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition first = new()
            {
                Name = "SignalA",
                MessageId = 0x321,
                MessageName = "MsgA",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            DbcCanSignalDefinition equivalent = new()
            {
                Name = "SignalA",
                MessageId = 0x321,
                MessageName = "MsgA",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };

            viewModel.ImportedDbcSignals.Add(first);
            viewModel.ImportedDbcSignals.Add(equivalent);
            viewModel.SetSelectedImportedDbcSignals([first, equivalent]);

            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            List<CustomCanSignal> storedSignals = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Single(storedSignals);
            Assert.Equal("SignalA", storedSignals[0].Name);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportSelectedDbcSignalToController_PartialRefreshDoesNotDropImportedSignalsOrNames()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition first = new()
            {
                Name = "CoolantTemp",
                MessageId = 0x320,
                MessageName = "MsgA",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            DbcCanSignalDefinition second = new()
            {
                Name = "OilPressure",
                MessageId = 0x321,
                MessageName = "MsgB",
                StartBit = 8,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            DbcCanSignalDefinition third = new()
            {
                Name = "FuelRate",
                MessageId = 0x322,
                MessageName = "MsgC",
                StartBit = 16,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };

            viewModel.ImportedDbcSignals.Add(first);
            viewModel.ImportedDbcSignals.Add(second);
            viewModel.ImportedDbcSignals.Add(third);
            viewModel.SetSelectedImportedDbcSignals([first, second, third]);

            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");
            Assert.Equal(3, viewModel.ControllerCustomCanSignals.Count());

            DataStructures partialSnapshot = new();
            partialSnapshot.CustomCanMessagesStaticData[0].Index = 0;
            partialSnapshot.CustomCanMessagesStaticData[0].Id = first.MessageId;
            partialSnapshot.CustomCanMessagesStaticData[0].Dlc = 8;
            partialSnapshot.CustomCanMessagesStaticData[0].Flags = 0x01;

            partialSnapshot.CustomCanMessagesStaticData[1].Index = 1;
            partialSnapshot.CustomCanMessagesStaticData[1].Id = second.MessageId;
            partialSnapshot.CustomCanMessagesStaticData[1].Dlc = 8;
            partialSnapshot.CustomCanMessagesStaticData[1].Flags = 0x01;

            partialSnapshot.CustomCanSignalsStaticData[0].Index = 0;
            partialSnapshot.CustomCanSignalsStaticData[0].Name = "Signal A";
            partialSnapshot.CustomCanSignalsStaticData[0].MessageName = "Message 1";
            partialSnapshot.CustomCanSignalsStaticData[0].MessageIndex = 0;
            partialSnapshot.CustomCanSignalsStaticData[0].StartBit = 0;
            partialSnapshot.CustomCanSignalsStaticData[0].BitLength = 8;
            partialSnapshot.CustomCanSignalsStaticData[0].Flags = 0x01;
            partialSnapshot.CustomCanSignalsStaticData[0].Factor = 1.0f;

            partialSnapshot.CustomCanSignalsStaticData[1].Index = 1;
            partialSnapshot.CustomCanSignalsStaticData[1].Name = "Signal B";
            partialSnapshot.CustomCanSignalsStaticData[1].MessageName = "Message 2";
            partialSnapshot.CustomCanSignalsStaticData[1].MessageIndex = 1;
            partialSnapshot.CustomCanSignalsStaticData[1].StartBit = 8;
            partialSnapshot.CustomCanSignalsStaticData[1].BitLength = 8;
            partialSnapshot.CustomCanSignalsStaticData[1].Flags = 0x01;
            partialSnapshot.CustomCanSignalsStaticData[1].Factor = 1.0f;

            InvokeUpdateLiveDataInPlace(viewModel, partialSnapshot);

            List<CustomCanSignal> storedSignals = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Equal(3, storedSignals.Count);
            Assert.Contains(storedSignals, signal => signal.Name == "CoolantTemp");
            Assert.Contains(storedSignals, signal => signal.Name == "OilPressure");
            Assert.Contains(storedSignals, signal => signal.Name == "FuelRate");
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportSelectedDbcSignalToController_PartialRefreshStillAllowsCheckingStoredSignals()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition first = new()
            {
                Name = "CoolantTemp",
                MessageId = 0x320,
                MessageName = "MsgA",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            DbcCanSignalDefinition second = new()
            {
                Name = "OilPressure",
                MessageId = 0x321,
                MessageName = "MsgB",
                StartBit = 8,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            DbcCanSignalDefinition third = new()
            {
                Name = "FuelRate",
                MessageId = 0x322,
                MessageName = "MsgC",
                StartBit = 16,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };

            viewModel.ImportedDbcSignals.Add(first);
            viewModel.ImportedDbcSignals.Add(second);
            viewModel.ImportedDbcSignals.Add(third);
            viewModel.SetSelectedImportedDbcSignals([first, second, third]);

            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            DataStructures partialSnapshot = new();
            partialSnapshot.CustomCanMessagesStaticData[0].Index = 0;
            partialSnapshot.CustomCanMessagesStaticData[0].Id = first.MessageId;
            partialSnapshot.CustomCanMessagesStaticData[0].Dlc = 8;
            partialSnapshot.CustomCanMessagesStaticData[0].Flags = 0x01;
            partialSnapshot.CustomCanMessagesStaticData[1].Index = 1;
            partialSnapshot.CustomCanMessagesStaticData[1].Id = second.MessageId;
            partialSnapshot.CustomCanMessagesStaticData[1].Dlc = 8;
            partialSnapshot.CustomCanMessagesStaticData[1].Flags = 0x01;

            partialSnapshot.CustomCanSignalsStaticData[0].Index = 0;
            partialSnapshot.CustomCanSignalsStaticData[0].Name = "Signal A";
            partialSnapshot.CustomCanSignalsStaticData[0].MessageName = "Message 1";
            partialSnapshot.CustomCanSignalsStaticData[0].MessageIndex = 0;
            partialSnapshot.CustomCanSignalsStaticData[0].StartBit = 0;
            partialSnapshot.CustomCanSignalsStaticData[0].BitLength = 8;
            partialSnapshot.CustomCanSignalsStaticData[0].Flags = 0x01;
            partialSnapshot.CustomCanSignalsStaticData[0].Factor = 1.0f;

            partialSnapshot.CustomCanSignalsStaticData[1].Index = 1;
            partialSnapshot.CustomCanSignalsStaticData[1].Name = "Signal B";
            partialSnapshot.CustomCanSignalsStaticData[1].MessageName = "Message 2";
            partialSnapshot.CustomCanSignalsStaticData[1].MessageIndex = 1;
            partialSnapshot.CustomCanSignalsStaticData[1].StartBit = 8;
            partialSnapshot.CustomCanSignalsStaticData[1].BitLength = 8;
            partialSnapshot.CustomCanSignalsStaticData[1].Flags = 0x01;
            partialSnapshot.CustomCanSignalsStaticData[1].Factor = 1.0f;

            InvokeUpdateLiveDataInPlace(viewModel, partialSnapshot);

            CustomCanSignal firstStored = viewModel.ControllerCustomCanSignals.First();
            firstStored.IsMarkedForDeletion = true;

            Assert.True(firstStored.IsMarkedForDeletion);
            Assert.True(viewModel.HasCheckedStoredSignal);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportedName_FollowsSignalWhenControllerMovesItToAnotherSlot()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition definition = new()
            {
                Name = "CoolantTemp",
                MessageId = 0x320,
                MessageName = "EngineData",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            viewModel.ImportedDbcSignals.Add(definition);
            viewModel.SetSelectedImportedDbcSignals([definition]);
            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            DataStructures snapshot = new();
            snapshot.CustomCanMessagesStaticData[0].Id = 0x999;
            snapshot.CustomCanMessagesStaticData[0].Dlc = 8;
            snapshot.CustomCanMessagesStaticData[0].Flags = 0x00;
            snapshot.CustomCanMessagesStaticData[2].Index = 2;
            snapshot.CustomCanMessagesStaticData[2].Id = 0x320;
            snapshot.CustomCanMessagesStaticData[2].Dlc = 8;
            snapshot.CustomCanMessagesStaticData[2].Flags = 0x01;

            snapshot.CustomCanSignalsStaticData[0].BitLength = 8;
            snapshot.CustomCanSignalsStaticData[0].Flags = 0x00;
            snapshot.CustomCanSignalsStaticData[3].Index = 3;
            snapshot.CustomCanSignalsStaticData[3].MessageIndex = 2;
            snapshot.CustomCanSignalsStaticData[3].StartBit = 0;
            snapshot.CustomCanSignalsStaticData[3].BitLength = 8;
            snapshot.CustomCanSignalsStaticData[3].Flags = 0x01;
            snapshot.CustomCanSignalsStaticData[3].Factor = 1.0f;

            InvokeUpdateLiveDataInPlace(viewModel, snapshot);

            CustomCanSignal stored = Assert.Single(viewModel.ControllerCustomCanSignals);
            Assert.Equal(3, stored.Index);
            Assert.Equal("CoolantTemp", stored.Name);
            Assert.Equal("EngineData", stored.MessageName);
            Assert.Equal("EngineData", viewModel.SettingsDataView.CustomCanMessagesStaticData[2].Name);
            Assert.Equal("Message 1", viewModel.SettingsDataView.CustomCanMessagesStaticData[0].Name);
            Assert.NotEqual("CoolantTemp", viewModel.SettingsDataView.CustomCanSignalsStaticData[0].Name);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ReusedSlot_DoesNotInheritPreviousSignalOrMessageName()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition definition = new()
            {
                Name = "CoolantTemp",
                MessageId = 0x320,
                MessageName = "EngineData",
                StartBit = 0,
                BitLength = 8,
                Factor = 1.0f,
                Dlc = 8,
            };
            viewModel.ImportedDbcSignals.Add(definition);
            viewModel.SetSelectedImportedDbcSignals([definition]);
            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            CustomCanSignal imported = Assert.Single(viewModel.ControllerCustomCanSignals);
            int slot = imported.Index;
            int messageSlot = imported.MessageIndex;

            DataStructures snapshot = new();
            snapshot.CustomCanMessagesStaticData[messageSlot].Index = messageSlot;
            snapshot.CustomCanMessagesStaticData[messageSlot].Id = 0x555;
            snapshot.CustomCanMessagesStaticData[messageSlot].Dlc = 8;
            snapshot.CustomCanMessagesStaticData[messageSlot].Flags = 0x01;
            snapshot.CustomCanSignalsStaticData[slot].Index = slot;
            snapshot.CustomCanSignalsStaticData[slot].MessageIndex = (byte)messageSlot;
            snapshot.CustomCanSignalsStaticData[slot].StartBit = 16;
            snapshot.CustomCanSignalsStaticData[slot].BitLength = 16;
            snapshot.CustomCanSignalsStaticData[slot].Flags = 0x01;
            snapshot.CustomCanSignalsStaticData[slot].Factor = 1.0f;

            InvokeUpdateLiveDataInPlace(viewModel, snapshot);

            CustomCanSignal reused = viewModel.SettingsDataView.CustomCanSignalsStaticData[slot];
            Assert.Equal($"Signal {slot + 1}", reused.Name);
            Assert.Equal($"Message {messageSlot + 1}", viewModel.SettingsDataView.CustomCanMessagesStaticData[messageSlot].Name);
            Assert.Contains(viewModel.SettingsDataView.CustomCanSignalsMetadata,
                metadata => metadata.Name == "CoolantTemp" && metadata.MessageId == 0x320u);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ImportedNames_PersistToCache_AndSurviveSettingsReplacement()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        MainWindowViewModel? reloaded = null;

        try
        {
            DbcCanSignalDefinition definition = new()
            {
                Name = "OilPressureCached",
                MessageId = 0x18FF1234,
                MessageName = "OilData",
                StartBit = 8,
                BitLength = 16,
                Factor = 0.1f,
                Dlc = 8,
            };
            viewModel.ImportedDbcSignals.Add(definition);
            viewModel.SetSelectedImportedDbcSignals([definition]);
            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            MethodInfo? replace = typeof(MainWindowViewModel).GetMethod("ReplaceSettingsData", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(replace);
            replace!.Invoke(viewModel, [new DataStructures()]);
            Assert.Contains(viewModel.SettingsDataView.CustomCanSignalsMetadata, metadata => metadata.Name == "OilPressureCached");

            reloaded = new MainWindowViewModel(new TestAppCloser());
            StopUiUpdateTimer(reloaded);
            CustomCanSignalMetadata cached = Assert.Single(reloaded.SettingsDataView.CustomCanSignalsMetadata, metadata => metadata.Name == "OilPressureCached");
            Assert.Equal(0x18FF1234u, cached.MessageId);
            Assert.True(cached.IsExtended);
            Assert.Equal("OilData", cached.MessageName);
            Assert.DoesNotContain(reloaded.SettingsDataView.CustomCanSignalsMetadata, metadata => metadata.Name.StartsWith("Signal ", System.StringComparison.Ordinal));
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
            if (reloaded != null)
            {
                StopUiUpdateTimer(reloaded);
            }
        }
    }

    [Fact]
    public void SetSelectedImportedDbcSignals_KeepsGridPrimaryItem_ForDownwardExtendedSelection()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition a = new() { Name = "A", MessageId = 0x100, StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 };
            DbcCanSignalDefinition b = new() { Name = "B", MessageId = 0x100, StartBit = 8, BitLength = 8, Factor = 1.0f, Dlc = 8 };
            DbcCanSignalDefinition c = new() { Name = "C", MessageId = 0x100, StartBit = 16, BitLength = 8, Factor = 1.0f, Dlc = 8 };
            viewModel.ImportedDbcSignals.Add(a);
            viewModel.ImportedDbcSignals.Add(b);
            viewModel.ImportedDbcSignals.Add(c);

            viewModel.SetSelectedImportedDbcSignals([a], a);
            viewModel.SetSelectedImportedDbcSignals([a, b, c], a);

            Assert.Same(a, viewModel.SelectedImportedDbcSignal);
            Assert.Equal(3, viewModel.SelectedImportedDbcSignals.Count);

            viewModel.SetSelectedImportedDbcSignals([b, c], c);
            Assert.Same(c, viewModel.SelectedImportedDbcSignal);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ParseDbc_UsesBit31AsExtendedMarker_AndKeepsStdAndExtSeparate()
    {
        const string dbc = """
            BO_ 256 StdMsg: 8 ECU
             SG_ StdSig : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            BO_ 2147483904 ExtMsg: 8 ECU
             SG_ ExtSig : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            BO_ 2364539904 J1939Msg: 8 ECU
             SG_ J1939Sig : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            """;

        (List<DbcCanMessageDefinition> messages, List<DbcCanSignalDefinition> signals) = InvokeParseDbc(dbc);

        Assert.Equal(3, messages.Count);
        DbcCanSignalDefinition std = signals.Single(s => s.Name == "StdSig");
        DbcCanSignalDefinition ext = signals.Single(s => s.Name == "ExtSig");
        DbcCanSignalDefinition j1939 = signals.Single(s => s.Name == "J1939Sig");
        Assert.Equal(0x100u, std.MessageId);
        Assert.False(std.IsExtended);
        Assert.Equal(0x100u, ext.MessageId);
        Assert.True(ext.IsExtended);
        Assert.Equal(0x0CF00400u, j1939.MessageId);
        Assert.True(j1939.IsExtended);
    }

    [Fact]
    public async Task ManualSave_LowExtendedId_IsStoredWithExtendedFlag_InSeparateSlotFromStandard()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.ManualCanMessageIdHex = "0x100";
            viewModel.ManualSignalName = "StdSig";
            await InvokeNonPublicTaskMethod(viewModel, "SaveManualCanSignalToController");

            viewModel.ManualCanIsExtended = true;
            viewModel.ManualSignalName = "ExtSig";
            await InvokeNonPublicTaskMethod(viewModel, "SaveManualCanSignalToController");

            List<CustomCanSignal> stored = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Equal(2, stored.Count);
            CustomCanMessage stdMessage = viewModel.SettingsDataView.CustomCanMessagesStaticData[stored.Single(s => s.Name == "StdSig").MessageIndex];
            CustomCanMessage extMessage = viewModel.SettingsDataView.CustomCanMessagesStaticData[stored.Single(s => s.Name == "ExtSig").MessageIndex];
            Assert.NotSame(stdMessage, extMessage);
            Assert.Equal(0x100u, stdMessage.Id);
            Assert.False(stdMessage.IsExtended);
            Assert.Equal(0x100u, extMessage.Id);
            Assert.True(extMessage.IsExtended);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Theory]
    [InlineData(nameof(MainWindowViewModel.ManualCanMessageIdHex), "0x800")]
    [InlineData(nameof(MainWindowViewModel.ManualCanMessageIdHex), "xyz")]
    [InlineData(nameof(MainWindowViewModel.ManualCanTimeoutMs), "abc")]
    [InlineData(nameof(MainWindowViewModel.ManualCanTimeoutMs), "60001")]
    [InlineData(nameof(MainWindowViewModel.ManualCanDlc), "0")]
    [InlineData(nameof(MainWindowViewModel.ManualCanDlc), "9")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalStartBit), "64")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalStartBit), "60")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalBitLength), "0")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalFactor), "NaN")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalFactor), "0")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalOffset), "1e40")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalName), "  ")]
    [InlineData(nameof(MainWindowViewModel.ManualSignalMinValue), "low")]
    public async Task ManualSave_InvalidField_ReportsErrorAndDoesNotSave(string propertyName, string value)
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            Assert.False(viewModel.HasManualCanValidationErrors);
            typeof(MainWindowViewModel).GetProperty(propertyName)!.SetValue(viewModel, value);

            Assert.True(viewModel.HasManualCanValidationErrors);
            Assert.False(viewModel.CanSaveManualCanSignal);
            string propertyToCheck = propertyName == nameof(MainWindowViewModel.ManualSignalStartBit) && value == "60"
                ? nameof(MainWindowViewModel.ManualSignalBitLength)
                : propertyName;
            Assert.NotEmpty(viewModel.GetErrors(propertyToCheck).Cast<string>());

            await InvokeNonPublicTaskMethod(viewModel, "SaveManualCanSignalToController");
            Assert.Empty(viewModel.ControllerCustomCanSignals);
            Assert.StartsWith("Manual signal not saved", viewModel.CustomCanStatusMessage);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ManualValidation_CrossFieldRules_TrackDependentFields()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.ManualCanMessageIdHex = "0x18FF50E5";
            Assert.True(viewModel.HasManualCanValidationErrors);
            viewModel.ManualCanIsExtended = true;
            Assert.False(viewModel.HasManualCanValidationErrors);

            viewModel.ManualSignalMinValue = "10";
            viewModel.ManualSignalMaxValue = "5";
            Assert.NotEmpty(viewModel.GetErrors(nameof(MainWindowViewModel.ManualSignalMaxValue)).Cast<string>());
            viewModel.ManualSignalMaxValue = "20";
            Assert.False(viewModel.HasManualCanValidationErrors);

            // Intel start 7 + 8 bits overruns a 1-byte frame; Motorola start 7 + 8 bits fits exactly.
            viewModel.ManualCanDlc = "1";
            viewModel.ManualSignalStartBit = "7";
            Assert.True(viewModel.HasManualCanValidationErrors);
            viewModel.ManualSignalIsMotorola = true;
            Assert.False(viewModel.HasManualCanValidationErrors);

            viewModel.ManualCanTimeoutMs = "0";
            Assert.False(viewModel.HasManualCanValidationErrors);
            viewModel.ManualDirectionIndex = 2;
            Assert.NotEmpty(viewModel.GetErrors(nameof(MainWindowViewModel.ManualCanTimeoutMs)).Cast<string>());
            viewModel.ManualDirectionIndex = 1;
            Assert.False(viewModel.HasManualCanValidationErrors);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ApplySelectedStoredDirection_OnlyChangesCheckedMessage()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DbcCanSignalDefinition[] defs =
            [
                new() { Name = "A", MessageId = 0x301, MessageName = "MsgA", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new() { Name = "B", MessageId = 0x302, MessageName = "MsgB", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new() { Name = "C", MessageId = 0x303, MessageName = "MsgC", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 },
            ];
            foreach (DbcCanSignalDefinition def in defs)
            {
                viewModel.ImportedDbcSignals.Add(def);
            }

            viewModel.SetSelectedImportedDbcSignals(defs);
            await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");

            List<CustomCanSignal> stored = viewModel.ControllerCustomCanSignals.ToList();
            Assert.Equal(3, stored.Select(s => s.MessageIndex).Distinct().Count());

            CustomCanSignal target = stored.Single(s => s.Name == "B");
            viewModel.SettingsDataView.CustomCanMessagesStaticData[target.MessageIndex].IsSelected = true;
            viewModel.SelectedStoredDirectionIndex = 1;
            InvokeNonPublicMethod(viewModel, "ApplySelectedStoredDirection");

            foreach (CustomCanSignal signal in viewModel.ControllerCustomCanSignals)
            {
                CustomCanMessage message = viewModel.SettingsDataView.CustomCanMessagesStaticData[signal.MessageIndex];
                Assert.Equal(signal.Name == "B", message.IsTx);
            }
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    private static async Task ImportAsync(MainWindowViewModel viewModel, params DbcCanSignalDefinition[] defs)
    {
        foreach (DbcCanSignalDefinition def in defs)
        {
            viewModel.ImportedDbcSignals.Add(def);
        }

        viewModel.SetSelectedImportedDbcSignals(defs);
        await InvokeNonPublicTaskMethod(viewModel, "ImportSelectedDbcSignalToController");
    }

    [Fact]
    public async Task StoredDirectionScopeSummary_ListsEveryCheckedMessageAndItsSignalCount()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            await ImportAsync(viewModel,
                new DbcCanSignalDefinition { Name = "A", MessageId = 0x97, MessageName = "BMS", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new DbcCanSignalDefinition { Name = "B", MessageId = 0x97, MessageName = "BMS", StartBit = 8, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new DbcCanSignalDefinition { Name = "C", MessageId = 0x98, MessageName = "Other", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 });

            Assert.Equal(string.Empty, viewModel.StoredDirectionScopeSummary);
            Assert.False(viewModel.HasCheckedStoredMessage);

            viewModel.ControllerCustomCanMessages.Single(m => m.Id == 0x97).IsSelected = true;

            Assert.True(viewModel.HasCheckedStoredMessage);
            string summary = viewModel.StoredDirectionScopeSummary;
            Assert.Contains("0x97 (BMS): 2 signals", summary);
            Assert.DoesNotContain("0x98", summary);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task StoredMessagesView_ShowsOneRowPerMessage_AndSignalsOfSelectedMessage()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            await ImportAsync(viewModel,
                new DbcCanSignalDefinition { Name = "A", MessageId = 0x97, MessageName = "BMS", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new DbcCanSignalDefinition { Name = "B", MessageId = 0x97, MessageName = "BMS", StartBit = 8, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new DbcCanSignalDefinition { Name = "C", MessageId = 0x98, MessageName = "Other", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 });

            Assert.Equal(2, viewModel.ControllerCustomCanMessages.Count);
            CustomCanMessage bms = viewModel.ControllerCustomCanMessages.Single(m => m.Id == 0x97);
            Assert.Equal("BMS", bms.Name);
            Assert.Equal(2, bms.SignalCount);

            viewModel.SelectedControllerCustomCanMessage = bms;
            Assert.Equal(new[] { "A", "B" }, viewModel.SelectedMessageCustomCanSignals.Select(s => s.Name).ToArray());
            Assert.Contains("0x97 BMS", viewModel.SelectedMessageSignalsHeader);

            viewModel.SelectedMessageCustomCanSignals[0].IsMarkedForDeletion = true;
            viewModel.SelectedControllerCustomCanMessage = viewModel.ControllerCustomCanMessages.Single(m => m.Id == 0x98);
            Assert.Equal(new[] { "C" }, viewModel.SelectedMessageCustomCanSignals.Select(s => s.Name).ToArray());
            // Delete-ticks on signals that scroll out of view are dropped so they cannot be deleted unseen.
            Assert.False(viewModel.HasCheckedStoredSignal);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task DeleteCheckedStoredMessages_RemovesMessageAndAllItsSignals()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            await ImportAsync(viewModel,
                new DbcCanSignalDefinition { Name = "A", MessageId = 0x97, MessageName = "BMS", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new DbcCanSignalDefinition { Name = "B", MessageId = 0x97, MessageName = "BMS", StartBit = 8, BitLength = 8, Factor = 1.0f, Dlc = 8 },
                new DbcCanSignalDefinition { Name = "C", MessageId = 0x98, MessageName = "Other", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 });

            CustomCanMessage bms = viewModel.ControllerCustomCanMessages.Single(m => m.Id == 0x97);
            bms.IsSelected = true;
            InvokeNonPublicMethod(viewModel, "DeleteCheckedStoredMessages");

            Assert.False(bms.IsEnabled);
            Assert.False(bms.IsSelected);
            CustomCanMessage remaining = Assert.Single(viewModel.ControllerCustomCanMessages);
            Assert.Equal(0x98u, remaining.Id);
            Assert.Equal(new[] { "C" }, viewModel.ControllerCustomCanSignals.Select(s => s.Name).ToArray());
            Assert.Same(remaining, viewModel.SelectedControllerCustomCanMessage);
            Assert.True(viewModel.HasPendingConfigChanges);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ManualEntry_NewMessageWithStoredId_IsRejected_UntilExistingMessageIsChosen()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            await ImportAsync(viewModel,
                new DbcCanSignalDefinition { Name = "A", MessageId = 0x97, MessageName = "BMS", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 8 });

            viewModel.ManualCanMessageIdHex = "0x97";
            Assert.Contains("already stored as BMS",
                viewModel.GetErrors(nameof(MainWindowViewModel.ManualCanMessageIdHex)).Cast<string>().Single());
            Assert.False(viewModel.CanSaveManualCanSignal);

            // The same ID as an extended frame is a different message.
            viewModel.ManualCanIsExtended = true;
            Assert.Empty(viewModel.GetErrors(nameof(MainWindowViewModel.ManualCanMessageIdHex)).Cast<string>());
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ManualEntry_AddSignalToExistingMessage_UsesThatFrame()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            await ImportAsync(viewModel,
                new DbcCanSignalDefinition { Name = "A", MessageId = 0x97, MessageName = "BMS", StartBit = 0, BitLength = 8, Factor = 1.0f, Dlc = 6 });
            CustomCanMessage bms = viewModel.ControllerCustomCanMessages.Single();
            bms.TimeoutMs = 750;

            CustomCanMessageTargetOption option = viewModel.ManualCanMessageTargets.Single(o => ReferenceEquals(o.Message, bms));
            viewModel.ManualCanMessageTarget = option;

            Assert.False(viewModel.IsManualNewMessage);
            Assert.Equal("0x97", viewModel.ManualCanMessageIdHex);
            Assert.Equal("6", viewModel.ManualCanDlc);
            Assert.Equal("750", viewModel.ManualCanTimeoutMs);
            Assert.Equal("BMS", viewModel.ManualMessageName);

            viewModel.ManualSignalName = "Voltage";
            viewModel.ManualSignalStartBit = "8";
            viewModel.ManualSignalBitLength = "16";
            Assert.False(viewModel.HasManualCanValidationErrors, viewModel.ManualCanValidationSummary);

            await InvokeNonPublicTaskMethod(viewModel, "SaveManualCanSignalToController");

            Assert.Single(viewModel.ControllerCustomCanMessages);
            Assert.Equal(2, bms.SignalCount);
            CustomCanSignal voltage = viewModel.ControllerCustomCanSignals.Single(s => s.Name == "Voltage");
            Assert.Same(bms, voltage.OwnerMessage);
            Assert.Equal("BMS", voltage.MessageName);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task ManualEntry_SavingNewMessage_SwitchesTargetToTheNewFrame()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            viewModel.ManualMessageName = "Dash";
            viewModel.ManualCanMessageIdHex = "0x321";
            viewModel.ManualSignalName = "Speed";
            Assert.False(viewModel.HasManualCanValidationErrors, viewModel.ManualCanValidationSummary);

            await InvokeNonPublicTaskMethod(viewModel, "SaveManualCanSignalToController");

            CustomCanMessage dash = Assert.Single(viewModel.ControllerCustomCanMessages);
            Assert.Equal("Dash", dash.Name);
            Assert.Same(dash, viewModel.ManualCanMessageTarget?.Message);
            Assert.False(viewModel.IsManualNewMessage);
            Assert.False(viewModel.HasManualCanValidationErrors, viewModel.ManualCanValidationSummary);

            viewModel.ManualSignalName = "Rpm";
            viewModel.ManualSignalStartBit = "16";
            await InvokeNonPublicTaskMethod(viewModel, "SaveManualCanSignalToController");

            Assert.Single(viewModel.ControllerCustomCanMessages);
            Assert.Equal(2, dash.SignalCount);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    private static MainWindowViewModel CreateViewModel()
    {
        MainWindowViewModel viewModel = new(new TestAppCloser());
        StopUiUpdateTimer(viewModel);
        viewModel.SettingsDataView.CustomCanSignalsMetadata.Clear();
        return viewModel;
    }

    private static void InvokeUpdateLiveDataInPlace(MainWindowViewModel viewModel, DataStructures source)
    {
        MethodInfo? method = typeof(MainWindowViewModel).GetMethod("UpdateLiveDataInPlace", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [source]);
    }

    private static void InvokeNonPublicMethod(MainWindowViewModel viewModel, string methodName)
    {
        MethodInfo? method = typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(viewModel, null);
    }

    private static async Task InvokeNonPublicTaskMethod(MainWindowViewModel viewModel, string methodName)
    {
        MethodInfo? method = typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        object? result = method!.Invoke(viewModel, null);
        Task task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }

    private static (List<DbcCanMessageDefinition> messages, List<DbcCanSignalDefinition> signals) InvokeParseDbc(string dbc)
    {
        MethodInfo? method = typeof(MainWindowViewModel).GetMethod("ParseDbc", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] args = [dbc, null, null];
        method!.Invoke(null, args);

        List<DbcCanMessageDefinition> messages = Assert.IsType<List<DbcCanMessageDefinition>>(args[1]);
        List<DbcCanSignalDefinition> signals = Assert.IsType<List<DbcCanSignalDefinition>>(args[2]);
        return (messages, signals);
    }

    private static int InvokePrivateIntMethod(MainWindowViewModel viewModel, string methodName)
    {
        MethodInfo? method = typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        object? value = method!.Invoke(viewModel, null);
        return Assert.IsType<int>(value);
    }

    private static void SetPrivateField(MainWindowViewModel viewModel, string fieldName, object? value)
    {
        FieldInfo? field = typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(viewModel, value);
    }

    private static void StopUiUpdateTimer(MainWindowViewModel viewModel)
    {
        FieldInfo? field = typeof(MainWindowViewModel).GetField("_uiUpdateTimer", BindingFlags.NonPublic | BindingFlags.Instance);
        object? timerObject = field?.GetValue(viewModel);
        if (timerObject is System.Timers.Timer timer)
        {
            timer.Stop();
        }
    }

    private sealed class TestAppCloser : IAppCloser
    {
        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(true);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL") => Task.FromResult(true);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(true);
        public Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<PinUnlockDialogResult?>(null);
    }
}
