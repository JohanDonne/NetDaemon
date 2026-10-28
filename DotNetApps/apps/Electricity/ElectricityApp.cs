using HomeAssistantGenerated;
using NetDaemon.HassModel.Entities;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Threading.Tasks;

namespace DotNetApps.apps.Electricity;

[NetDaemonApp]
public class ElectricityApp
{
    private readonly ILogger<ElectricityApp> _logger;

    private readonly CultureInfo culture = new("en-US");
    private readonly IHaContext _ha;
    private readonly Entities _entities;

    private double actualTotalPower;
    private double actualBatteryPower;
    private double _chargerCurrent;
    private bool chargingSuspended = false;

    private const string CHARGING = "Charging";
    private const string AVAILABLE = "Available";
    private long _SuspendTime = 30;
    private double[] actualPower = new double[3];
    private int actualPowerIndex = 0;
    private double averageActualPower;

    private string _logMessage = string.Empty;
    private NumericSensorEntity consumptionEntity;
    private NumericSensorEntity injectionEntity;
    private InputNumberEntity actualTotalPowerEntity;
    private InputNumberEntity actualBatteryPowerEntity;
    private InputBooleanEntity enableChargerEntity;
    private InputBooleanEntity dynamicCharging;

    private InputBooleanEntity _solarOnly;
    private InputNumberEntity setChargerPowerEntity;
    private NumericSensorEntity voltageEntity;
    private NumberEntity chargerMaxCurrentEntity;
    private InputNumberEntity OfferedChargingPowerEntity;
    private InputNumberEntity netMaxPowerEntity;
    private SensorEntity chargerStatusEntity;
    private NumericSensorEntity chargerCurrentImportEntity;
    private NumericSensorEntity chargerCurrentOfferedEntity;
    private NumericSensorEntity batteryChargingPowerEntity;
    private NumericSensorEntity batteryDischargingPowerEntity;
    private NumericSensorEntity batterySocEntity;
    private NumericSensorEntity batteryPowerNetEntity;
    private AutomationEntity configureChargerEntity;

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
        _chargerCurrent = chargerCurrentOfferedEntity.State ?? 0.0;

        scheduler.SchedulePeriodic(TimeSpan.FromSeconds(2), () =>
        {
            UpdateActualTotalPower();
            UpdateChargerPower();
        });
        scheduler.SchedulePeriodic(TimeSpan.FromSeconds(60), async () =>
        {
            await CheckOccpConfiguration();
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

        chargerStatusEntity
           .StateChanges()
           .Where(e => e.New?.State == AVAILABLE)
           .Subscribe(s => ResetStaticChargingPower());
    }

    private void ConfigureApp(IAppConfig<ChargerConfig> config)
    {
        _SuspendTime = config.Value.SuspendTime;
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

    private void ResetStaticChargingPower()
    {
        // reset static charging power to 0 whenever CCS cable is disconnected
        setChargerPowerEntity.SetValue(0.0);
    }

    [MemberNotNull(nameof(consumptionEntity),
                     nameof(injectionEntity),
                     nameof(actualTotalPowerEntity),
                     nameof(enableChargerEntity),
                     nameof(dynamicCharging),
                     nameof(_solarOnly),
                     nameof(voltageEntity),
                     nameof(chargerMaxCurrentEntity),
                     nameof(OfferedChargingPowerEntity),
                     nameof(netMaxPowerEntity),
                     nameof(chargerStatusEntity),
                     nameof(chargerCurrentImportEntity),
                     nameof(chargerCurrentOfferedEntity),
                     nameof(setChargerPowerEntity),
                     nameof(actualTotalPowerEntity),
                     nameof(actualBatteryPowerEntity),
                     nameof(batteryChargingPowerEntity),
                     nameof(batteryDischargingPowerEntity),
                     nameof(batterySocEntity),
                     nameof(configureChargerEntity)
                  )]

    private void SetupEntities()
    {
        consumptionEntity = _entities.Sensor.ElectricityMeterEnergieverbruik;
        injectionEntity = _entities.Sensor.ElectricityMeterEnergieproductie;
        actualTotalPowerEntity = _entities.InputNumber.ActualTotalPower;
        actualBatteryPowerEntity = _entities.InputNumber.ActualBatteryPower;
        batteryChargingPowerEntity = _entities.Sensor.SolisS6Eh1pBatteryChargePower;
        batteryDischargingPowerEntity = _entities.Sensor.SolisS6Eh1pBatteryDischargePower;
        enableChargerEntity = _entities.InputBoolean.Enablecharger;
        dynamicCharging = _entities.InputBoolean.DynamicCharging;
        _solarOnly = new InputBooleanEntity(_ha, "input_boolean.solaronly");
        setChargerPowerEntity = _entities.InputNumber.SetChargerPower;
        voltageEntity = _entities.Sensor.ElectricityMeterSpanningFaseL1;
        chargerMaxCurrentEntity = _entities.Number.ChargerMaximumCurrent2;
        OfferedChargingPowerEntity = _entities.InputNumber.OfferedChargingPower;
        netMaxPowerEntity = _entities.InputNumber.NetMaxPower;
        chargerStatusEntity = _entities.Sensor.ChargerStatusConnector;
        chargerCurrentImportEntity = _entities.Sensor.ChargerCurrentImport;
        chargerCurrentOfferedEntity = _entities.Sensor.ChargerCurrentOffered2;
        batterySocEntity = _entities.Sensor.SolisS6Eh1pBatterySoc;
        configureChargerEntity = _entities.Automation.ConfigureOcppCharger;
        batteryPowerNetEntity = _entities.Sensor.SolisBatteryPowerNet;
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
        actualPower[actualPowerIndex] = actualTotalPower;
        actualPowerIndex = (actualPowerIndex + 1) % 3;
        averageActualPower = (actualPower[0] + actualPower[1] + actualPower[2]) / 3.0;
        actualTotalPowerEntity.SetValue(actualTotalPower);
    }

    private void UpdateActualBatteryPower()
    {
        // calculate Actual Total Power import from grid
        try
        {
            //double chargingValue = batteryChargingPowerEntity.State ?? 0.0;
            //double dischargingValue = batteryDischargingPowerEntity.State ?? 0.0;
            //actualBatteryPower = chargingValue - dischargingValue;
            actualBatteryPower = batteryPowerNetEntity.State ?? 0.0;
        }
        catch
        {
            actualBatteryPower = 0.0;
        }
        actualBatteryPowerEntity.SetValue(actualBatteryPower);
    }

    private async Task CheckOccpConfiguration()
    {
        if (chargerStatusEntity.State != CHARGING) return;
        await Task.Delay(30000);
        if (OfferedChargingPowerEntity.State > 1500.0 && chargerCurrentOfferedEntity.State < 1.0)
        {
            // charger does not report correct values, should be configured through OCPP
            configureChargerEntity.Trigger();
        }
    }

    private void UpdateChargerPower()
    {
        double current = (enableChargerEntity.IsOff(), dynamicCharging.IsOff(), _solarOnly.IsOn()) switch
        {
            (true, _, _) => 0.0,
            (false, true, _) => GetStaticPower(),
            (false, false, true) => GetSolarCurrent(),
            _ => GetDynamicCurrent()
        };
        SetChargerCurrent(current);
    }

    private double GetStaticPower()
    {
        double current = Math.Round((double)((voltageEntity.State != null) ? (setChargerPowerEntity.State ?? 0.0) / voltageEntity.State! : 0.0));
        _logMessage = "Static charging, ";
        return current;
    }

    private double GetDynamicCurrent()
    {
        UpdateActualBatteryPower();
        return GetDynamicChargerCurrent();
    }

    private double GetSolarCurrent()
    {
        UpdateActualBatteryPower();
        return chargerStatusEntity.State != CHARGING ? GetInitialSolarCurrent() : GetChargingSolarCurrent();
    }

    private double GetInitialSolarCurrent()
    {
        // calculate current offered to car

        double current;
        double powerBudget = GetSolarPowerBudget();

        current = Math.Floor((double)((voltageEntity.State != null) ? powerBudget / voltageEntity.State! : 0.0));
        // if the current offered is 4 or 5A and the SOC of the home battery is > 20%, set the current to 6A: better 
        // to drain the home battery a little bit to enable charging the car, than to inject the solar power surplus into the grid.
        if (3 < current && current < 6 && (batterySocEntity.State > 20.0)) current = 6.0;
        _logMessage = "SolarOnly, Starting, ";
        return current > 0.0 ? current : 0.0;
    }

    private double GetSolarPowerBudget()
    {
        double powerBudget = -1.0 * averageActualPower;
        // double powerBudget = averageActualPower > -100 ? 0 : -1.0 * averageActualPower;
        // if the home battery is charging and has a SOC > 20%, add its charging power to the power budget
        // (car always gets priority over the home battery)
        // We do not do this if home battery SOC is < 20%, because it may be forced charging.
        powerBudget += (actualBatteryPower < -100 && batterySocEntity.State > 20.0) ? -1.0 * actualBatteryPower : 0.0;
        return powerBudget;
    }

    private double GetChargingSolarCurrent()
    {
        double deltaCurrent;
        double powerBudget = GetSolarPowerBudget();
        deltaCurrent = Math.Floor((double)((voltageEntity.State != null) ? powerBudget / voltageEntity.State! : 0.0));
        var newcurrent = _chargerCurrent + deltaCurrent;
        if (3 < newcurrent && newcurrent < 6 && (batterySocEntity.State > 20.0)) newcurrent = 6.0;
        // if the current offered is 4 or 5A and the SOC of the home battery is > 20%, set the current to 6A: better 
        // to drain the home battery a little bit to enable charging the car, than to inject the solar power surplus into the grid.

        _logMessage = $"SolarOnly, Charging, ";
        return newcurrent > 0.0 ? newcurrent : 0.0;
    }

    private double GetDynamicChargerCurrent()
    {
        return chargerStatusEntity.State != CHARGING ? GetInitialDynamicCurrent() : GetChargingDynamicCurrent();
    }

    private double GetInitialDynamicCurrent()
    {
        double powerBudget = GetPowerBudget();
        double current = Math.Floor((double)((voltageEntity.State != null) ? powerBudget / voltageEntity.State! : 0.0));
        _logMessage = $"Dynamic charging, Starting, ";
        return current > 0.0 ? current : 0.0;
    }

    private double GetChargingDynamicCurrent()
    {
        double deltaBudget = GetPowerBudget();
        double current = Math.Floor(_chargerCurrent + (double)((voltageEntity.State != null) ? deltaBudget / voltageEntity.State! : 0.0));
        _logMessage = $"Dynamic charging, Charging, ";
        return current > 0.0 ? current : 0.0;
    }

    private double GetPowerBudget()
    {
        double budget = (netMaxPowerEntity.State ?? 0.0) - averageActualPower;
        if ((batterySocEntity.State > 20.0) && (actualBatteryPower < -100)) budget = budget - actualBatteryPower;
        return budget;
    }

    private void SetChargerCurrent(double current)
    {
        if (current < 0.0) current = 0.0;
        // while charging, only update charger current increase if the car has reacted to the previous increase, to prevent overshoots 
        // due to deadtime between charger and car. The car may take a few seconds to react to the new offered current,
        // and if the offered current is increased again before the car has reacted, it may overshoot the desired charging power.
        if (chargerStatusEntity.State == CHARGING)
        {
            double delta = (chargerCurrentImportEntity.State ?? 0.0) - _chargerCurrent;
            if (delta < -1.0)
            {
                _logger.LogDebug($"Car is syncing, delta: {delta:F1}A");
                return;
            }
        }
        current = current < 32.0 ? current : 32.0;
        // only update the charger current if the difference is more than 0.5A, to prevent too many updates
        if ((_chargerCurrent - current) > 0.5 || (_chargerCurrent - current) < -0.5)
        {
            _logger.LogDebug($"{_logMessage}: curent set to {current}A");
            _chargerCurrent = current;
            chargerMaxCurrentEntity.SetValue(_chargerCurrent.ToString());
            OfferedChargingPowerEntity.SetValue(_chargerCurrent * voltageEntity.State ?? 0.0);
        }
    }
}
