using HomeAssistantGenerated;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Concurrency;

namespace DotNetApps.apps.Electricity;

[NetDaemonApp]
public class ElectricityApp
{
    private readonly ILogger<ElectricityApp> _logger;

    private readonly CultureInfo culture = new("en-US");
    private readonly IHaContext _ha;
    private readonly Entities _entities;

    private double actualTotalPower;
    private double chargerCurrent;
    private bool chargingSuspended = false;

    private const string CHARGING = "Charging";
    private long _SuspendTime = 30; 
    private double _PFactor = 1.0;

    private NumericSensorEntity consumptionEntity;
    private NumericSensorEntity injectionEntity;
    private InputNumberEntity actualTotalPowerEntity;
    private InputBooleanEntity enableChargerEntity;
    private InputBooleanEntity dynamicCharging;
    private InputNumberEntity setChargerPowerEntity;
    private NumericSensorEntity voltageEntity;
    private NumberEntity chargerMaxCurrentEntity;
    private InputNumberEntity OfferedChargingPowerEntity;
    private InputNumberEntity netMaxPowerEntity;
    private SensorEntity chargerStatusEntity;
    private NumericSensorEntity chargerCurrentImportEntity;
    private NumericSensorEntity chargerCurrentOfferedEntity;

    public ElectricityApp(IHaContext ha,
                          IScheduler scheduler,
                          Entities entities,
                          IAppConfig<ChargerConfig> config,
                          ILogger<ElectricityApp> logger)
    {
        _ha = ha;
        _entities = entities;
        _logger = logger;
        ConfigureApp(config);
        SetupEntities();
        chargerCurrent = chargerCurrentOfferedEntity.State ?? 0.0;

        scheduler.SchedulePeriodic(TimeSpan.FromSeconds(2), () =>
        {
            UpdateActualTotalPower();
            UpdateChargerPower();
        });

        chargerStatusEntity
            .StateChanges()
            .Where(e => e.Old?.State == CHARGING)
            .Subscribe(s => SuspendCharging());

        chargerStatusEntity
            .StateChanges()
            .Where(e => e.Old?.State == CHARGING)
            .WhenStateIsFor(s => s?.State != CHARGING, TimeSpan.FromSeconds(_SuspendTime), scheduler)
            .Subscribe(s => ResumeCharging());
    }

    private void ConfigureApp(IAppConfig<ChargerConfig> config)
    {
        _SuspendTime = config.Value.SuspendTime;
        _PFactor = config.Value.PFactor;
    }

    private void ResumeCharging()
    {
        enableChargerEntity.TurnOn();
        chargingSuspended = false;
    }
   

    private void SuspendCharging()
    {
        enableChargerEntity.TurnOff();
        chargingSuspended = true;
    }

    [MemberNotNull(nameof(consumptionEntity),
                     nameof(injectionEntity),
                     nameof(actualTotalPowerEntity),
                     nameof(enableChargerEntity),
                     nameof(dynamicCharging),
                     nameof(voltageEntity),
                     nameof(chargerMaxCurrentEntity),
                     nameof(OfferedChargingPowerEntity),
                     nameof(netMaxPowerEntity),
                     nameof(chargerStatusEntity),
                     nameof(chargerCurrentImportEntity),
                     nameof(chargerCurrentOfferedEntity),
                     nameof(setChargerPowerEntity)
                  )]
    private void SetupEntities()
    {
        consumptionEntity = _entities.Sensor.ElectricityMeterEnergieverbruik;
        injectionEntity = _entities.Sensor.ElectricityMeterEnergieproductie;
        actualTotalPowerEntity = _entities.InputNumber.ActualTotalPower;
        enableChargerEntity = _entities.InputBoolean.Enablecharger;
        dynamicCharging = _entities.InputBoolean.DynamicCharging;
        setChargerPowerEntity = _entities.InputNumber.SetChargerPower;
        voltageEntity = _entities.Sensor.ElectricityMeterSpanningFaseL1;
        chargerMaxCurrentEntity = _entities.Number.ChargerMaximumCurrent;
        OfferedChargingPowerEntity = _entities.InputNumber.OfferedChargingPower;
        netMaxPowerEntity = _entities.InputNumber.NetMaxPower;
        chargerStatusEntity = _entities.Sensor.ChargerStatusConnector;
        chargerCurrentImportEntity = _entities.Sensor.ChargerCurrentImport;
        chargerCurrentOfferedEntity = _entities.Sensor.ChargerCurrentOffered;
    }

    private void UpdateActualTotalPower()
    {
        // calculate Actual Total Power import from grid
        try
        {
            double consumptionValue = consumptionEntity.State ?? 0.0;
            double injectionValue = injectionEntity.State ?? 0.0;
            actualTotalPower = (consumptionValue - injectionValue) * 1000; // convert from kW > Watt
        }
        catch
        {
            actualTotalPower = 0.0;
        }
        actualTotalPowerEntity.SetValue(actualTotalPower);
    }

    private void UpdateChargerPower()
    {
        double current;
        if (enableChargerEntity.IsOff())
        {
            SetChargerCurrent(0);
            return;
        }
        if (dynamicCharging.IsOff())
        {
            current = Math.Round((double)((voltageEntity.State != null) ? (setChargerPowerEntity.State ?? 0.0) / voltageEntity.State! : 0.0));
            _logger.LogDebug($"Static, current > {current}");
            ; SetChargerCurrent(current);
            return;
        }
        current = GetDynamicCurrent();
        SetChargerCurrent(current);
    }

    private double GetDynamicCurrent()
    {
        double current;
        if (chargerStatusEntity.State != CHARGING)
        {
            double powerBudget = (netMaxPowerEntity.State ?? 0.0) - actualTotalPower;
            current = Math.Floor((double)((voltageEntity.State != null) ? powerBudget / voltageEntity.State! : 0.0));
            _logger.LogDebug($"Not charging, current > {current}");
            return current > 0.0 ? current : 0.0;
        }
        // Check if car is actually charging 
        // This is to prevent changing the offered power while the car/charger is still
        // syncing to the previous setting 

        double delta = (chargerCurrentImportEntity.State ?? 0.0) - chargerCurrent;
        if ((delta < -1.0) || (delta > 1.0))
        {
            _logger.LogDebug($"Car is syncing, delta: {delta}");
            return chargerCurrent;
        }

        // if actually charging calculate new current
        double deltaBudget = (netMaxPowerEntity.State ?? 0.0) - actualTotalPower;
        current = Math.Floor(chargerCurrent + (double)((voltageEntity.State != null) ? deltaBudget * _PFactor / voltageEntity.State! : 0.0));
        _logger.LogDebug($"Charging, current > {current}");
        return current;
    }

    private void SetChargerCurrent(double current)
    {
        if (current < 0.0) current = 0.0;
        if ((chargerCurrent - current) > 0.5 || (chargerCurrent - current) < -0.5)
        {
            current = current < 31.0 ? current : 31.0;
            chargerCurrent = current;
            chargerMaxCurrentEntity.SetValue(chargerCurrent.ToString());
            OfferedChargingPowerEntity.SetValue(chargerCurrent * voltageEntity.State ?? 0.0);
        }
    }
}
