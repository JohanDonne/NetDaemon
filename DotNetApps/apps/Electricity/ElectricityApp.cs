using HomeAssistantGenerated;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel;
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
    private double chargerCurrent;
    private bool chargingSuspended = false;

    private const string CHARGING = "Charging";
    private const string AVAILABLE = "Available";
    private long _SuspendTime = 30; 
    private double _PFactor = 1.0;
    private double[] actualPower = new double[3];
    private int actualPowerIndex = 0;
    private double averageActualPower;

    private NumericSensorEntity consumptionEntity;
    private NumericSensorEntity injectionEntity;
    private InputNumberEntity actualTotalPowerEntity;
    private InputNumberEntity actualBatteryPowerEntity;
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
    private NumericSensorEntity batteryChargingPowerEntity;
    private NumericSensorEntity batteryDischargingPowerEntity;
    private NumericSensorEntity batterySocEntity;
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
        chargerCurrent = chargerCurrentOfferedEntity.State ?? 0.0;

        scheduler.SchedulePeriodic(TimeSpan.FromSeconds(2), () =>
        {
            UpdateActualTotalPower();
            UpdateActualBatteryPower();
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
        setChargerPowerEntity = _entities.InputNumber.SetChargerPower;
        voltageEntity = _entities.Sensor.ElectricityMeterSpanningFaseL1;
        chargerMaxCurrentEntity = _entities.Number.ChargerMaximumCurrent;
        OfferedChargingPowerEntity = _entities.InputNumber.OfferedChargingPower;
        netMaxPowerEntity = _entities.InputNumber.NetMaxPower;
        chargerStatusEntity = _entities.Sensor.ChargerStatusConnector;
        chargerCurrentImportEntity = _entities.Sensor.ChargerCurrentImport;
        chargerCurrentOfferedEntity = _entities.Sensor.ChargerCurrentOffered;
        batterySocEntity = _entities.Sensor.SolisS6Eh1pBatterySoc;
        configureChargerEntity = _entities.Automation.ConfigureOcppCharger;
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
            double chargingValue = batteryChargingPowerEntity.State ?? 0.0;
            double dischargingValue = batteryDischargingPowerEntity.State ?? 0.0;
            actualBatteryPower = chargingValue - dischargingValue;
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
            SetChargerCurrent(current);
            return;
        }
        current = GetDynamicCurrent();
        SetChargerCurrent(current);
    }

    private double GetDynamicCurrent()
    {
        if ((netMaxPowerEntity.State ?? 0.0) > 0.0) return GetDynamicChargerCurrentWithCarPriority();
        return GetDynamicChargerCurrentWithHomeBatteryPriority();        
    }

    private double GetDynamicChargerCurrentWithHomeBatteryPriority()
    {
        double current;
        if (chargerStatusEntity.State != CHARGING)
        {
            // calculate current offered to car
            double powerBudget = averageActualPower > -100 ? 0 : -1.0 * averageActualPower;

            // the condition (500.0 < powerBudget && powerBudget < 1500.0) indicates one of two conditions:
            // 1. there is a surplus of solar power while the home battery is charging, but not enough to charge the car.
            //    In that case it's better to lower the charging budget for the home battery to the point where the car
            //    starts charging as well (otherwise the surplus solar power is injected into the grid).
            // 2. the home battery is fully charged and solar power surplus is not enough to charge the car. In that case
            //    it's better to take some power from the home battery to the point where the car starts charging.
            //    Otherwise the surplus solar power is injected into the grid.
            //    Note: the calculation for the case where the car is in the 'CHARGING' state should make sure that the home battery 
            //          is not discharged beyond an acceptable level 
            
            if ((500.0 < powerBudget && powerBudget < 1500.0)&& (actualBatteryPower > 2000 || batterySocEntity.State > 95.0)) powerBudget = 1500.0;
            current = Math.Floor((double)((voltageEntity.State != null) ? powerBudget / voltageEntity.State! : 0.0));
            _logger.LogDebug($"Not charging, current --> {current}");
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

        // if actually charging, calculate new current
        double deltaBudget;
        if (actualBatteryPower > 0)
        {
            // home battery is charging, get surplus power to car
            if (actualBatteryPower < 2500)
            {
                // lower car charging rate to try to charge home battery with at least 2500W 
                deltaBudget = actualBatteryPower - 2500;
            }
            else
            {
                deltaBudget = -1.0 * averageActualPower;
            }
        }
        else if ((-0.1 < actualBatteryPower) && (actualBatteryPower < 0.1))
        {
            // case where battery is full or there is not enough solar power to charge home battery
            deltaBudget = -1.0 * averageActualPower;
            // is battery is not fully charged, lower car charging power to allow home battery charging
            if (batterySocEntity.State < 95.0) deltaBudget = ( -1.0 * chargerCurrent * voltageEntity.State ?? 0.0) * 0.25;
        }
        else
        {
            // Battery is discharging to car, stop charging car
            deltaBudget =  actualBatteryPower;
        }
        // don't lower the power offered to the car when there is temporarily lower solar power
        // as long as the battery still has decent charging power or SOC
        // This is to avoid toggling the car charging current between 6 (charging) and 5 (not  charging)
        if (deltaBudget < 0 && chargerCurrent < 7.0 && actualBatteryPower > -1000.0 && (actualBatteryPower > 2000 || batterySocEntity.State > 95.0)) deltaBudget = 0;

        current = Math.Floor(chargerCurrent + (double)((voltageEntity.State != null) ? deltaBudget * _PFactor / voltageEntity.State! : 0.0));
        _logger.LogDebug($"Charging, averagePower: {averageActualPower:F0}, deltaBudget: {deltaBudget:F0}, actualBatteryPower: {actualBatteryPower:F0} ");
        _logger.LogDebug($"Charging, current --> {current}");
        return current; 
    }

    private double GetDynamicChargerCurrentWithCarPriority()
    {
        double current = 0.0;
        if (chargerStatusEntity.State != CHARGING)
        {
            double powerBudget = (netMaxPowerEntity.State ?? 0.0) - (averageActualPower - actualBatteryPower);
            current = Math.Floor((double)((voltageEntity.State != null) ? powerBudget / voltageEntity.State! : 0.0));
            _logger.LogDebug($"Car Priority,  Not charging, current --> {current}");
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
        //// if actually charging calculate new current
        double deltaBudget = (netMaxPowerEntity.State ?? 0.0) - (averageActualPower - actualBatteryPower);
        current = Math.Floor(chargerCurrent + (double)((voltageEntity.State != null) ? deltaBudget * _PFactor / voltageEntity.State! : 0.0));
        _logger.LogDebug($"Car Priority, Charging, current --> {current}");

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
