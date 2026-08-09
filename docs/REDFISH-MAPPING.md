# Redfish mapping table (Dell iDRAC)

**Required artifact — DESIGN §14 #3.** Exact endpoints per data point with
sample payloads from one fleet iDRAC. Captured live from HOST-A
(`https://10.x.x.x`, iDRAC9, PowerEdge R7415, AMD EPYC 7551P, PERC H330 Mini)
on 2026-08-09 via basic auth (`root`), iDRAC firmware generation 9.
Used to fix D4 — this is the ground truth the provider is written against.

## Endpoints polled per host per interval (60 s default)

| # | Endpoint | Data point | Component type | Payload shape |
|---|---|---|---|---|
| 1 | `GET /redfish/v1/Systems/System.Embedded.1` | Overall health (`Status.Health`/`HealthRollup`) | `system` (synthetic row) | single resource |
| 2 | `GET /redfish/v1/Systems/System.Embedded.1/Processors` | CPU inventory + health | `cpu` | **collection of link objects** |
| 3 | `GET /redfish/v1/Systems/System.Embedded.1/Memory` | DIMM inventory + health | `memory` | **collection of link objects** |
| 4 | `GET /redfish/v1/Systems/System.Embedded.1/Storage` | Storage controllers + physical disks | `controller`, `disk` | **collection of link objects** |
| 5 | `GET /redfish/v1/Chassis/System.Embedded.1/Thermal` | Temps (Celsius), fans (RPM) + health | `temp`, `fan` | inline arrays (`Temperatures`, `Fans`) |
| 6 | `GET /redfish/v1/Chassis/System.Embedded.1/Power` | PSU health, power draw | `psu`, metric `power:consumed` | inline arrays (`PowerSupplies`, `PowerControl`) |

Member resources of collections 2–4 are fetched individually by following each
member's `@odata.id` — iDRAC does **not** inline them without
`?$expand=*($levels=1)`, and even with expand the Drives links stay references.

## Collection shape (link-only members — the D4 trap)

```json
// GET /redfish/v1/Systems/System.Embedded.1/Processors  (HTTP 200)
{
  "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Processors",
  "Name": "ProcessorsCollection",
  "Members@odata.count": 1,
  "Members": [
    { "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1" }
  ]
}
```

`Memory` is identical with 8 members (`DIMM.Socket.A1` … `A8`); `Storage` has 5
members (`RAID.Integrated.1-1`, `AHCI.Slot.5-1`, `AHCI.Embedded.3-1`,
`PCIeSSD.Slot.2-C`, `PCIeSSD.Slot.3-C`). Members carry **no `Name`, no
`Status`** — a parser that requires `Name` inline silently drops everything.

## Member resources

### Processor — `.../Processors/CPU.Socket.1`

```json
{
  "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1",
  "Name": "CPU 1",
  "Socket": "CPU.Socket.1",
  "Model": "AMD EPYC 7551P 32-Core Processor",
  "Manufacturer": "AMD",
  "TotalCores": 32, "TotalThreads": 64, "MaxSpeedMHz": 3000,
  "Status": { "Health": "OK", "State": "Enabled" },
  "Oem": { "Dell": { "DellProcessor": { "CPUStatus": "CPUEnabled", "Cache1PrimaryStatus": "OK", "..." : "..." } } }
}
```

### Memory — `.../Memory/DIMM.Socket.A1`

```json
{
  "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A1",
  "Name": "DIMM A1",
  "DeviceLocator": "DIMM A1",
  "Manufacturer": "Samsung",
  "PartNumber": "M393A2K40BB2-CTD",
  "SerialNumber": "DIMMSN01",
  "CapacityMiB": 16384, "MemoryDeviceType": "DDR4", "OperatingSpeedMhz": 2666,
  "Status": { "Health": "OK", "State": "Enabled" }
}
```

### Storage controller — `.../Storage/RAID.Integrated.1-1`

```json
{
  "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1",
  "Name": "PERC H330 Mini",
  "Description": "PERC H330 Mini",
  "Status": { "Health": "OK", "HealthRollup": "OK", "State": "Enabled" },
  "Drives@odata.count": 1,
  "Drives": [
    { "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1/Drives/Disk.Bay.0:Enclosure.Internal.0-1:RAID.Integrated.1-1" }
  ],
  "Oem": { "Dell": { "DellController": { "ControllerFirmwareVersion": "25.5.6.0009", "RollupStatus": "OK", "..." : "..." } } }
}
```

Notes:

- Controllers with **no drives** return `"Drives": []` (`AHCI.Embedded.3-1`);
  `StorageControllers[]` in the same resource duplicates the controller health.
- On this fleet the Dell OEM controller detail lives in
  `Oem.Dell.DellController` **on the Storage resource**, not on the Chassis.
- `Status.Health`/`HealthRollup` can be `null` (AHCI controller) — fall back
  to `Status.State` (`Enabled` → OK).

### Physical disk — `.../Storage/<controller>/Drives/<id>`

```json
{
  "@odata.id": "/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1/Drives/Disk.Bay.0:Enclosure.Internal.0-1:RAID.Integrated.1-1",
  "Name": "Physical Disk 0:1:0",
  "Model": "TOSHIBA MG04ACA1",
  "Manufacturer": "TOSHIBA",
  "SerialNumber": "DRIVESN0001",
  "PartNumber": "EXAMPLEPARTNUMBER0000001",
  "CapacityBytes": 1000204886016,
  "MediaType": "HDD", "Protocol": "SATA", "RotationSpeedRPM": 8220, "Revision": "FK5D",
  "FailurePredicted": false,
  "Status": { "Health": "OK", "HealthRollup": "OK", "State": "Enabled" },
  "Oem": { "Dell": { "DellPhysicalDisk": { "PredictiveFailureState": "SmartAlertAbsent", "RaidStatus": "NonRAID", "..." : "..." } } }
}
```

Notes:

- **Predictive failure** surfaces as `FailurePredicted: true` (SMART alert;
  OEM `DellPhysicalDisk.PredictiveFailureState` = `SmartAlertPresent`). Some
  firmware keeps `Status.Health: OK` while setting it — the provider escalates
  to `warning` when `FailurePredicted` is true rather than trusting Status
  alone.
- NVMe/PCIe SSDs are the same `Drive` schema
  (`.../Storage/PCIeSSD.Slot.2-C/Drives/PCIeSSD.Slot.2-1`,
  `Name: "PCIe SSD in Slot 2"`, `MediaType: "SSD"`, `Status.Health` often
  absent → `State` fallback).

## Chassis OEM — verified dead end

`GET /redfish/v1/Chassis/System.Embedded.1` → `Oem.Dell` contains **only**
`DellChassis` on this iDRAC9. There is no `DellPhysicalDisk`/`DellController`
property on the Chassis resource — the pre-D4 provider looked there and found
nothing. Disks/controllers live in the System `Storage` tree (above).

## Thermal / Power (inline — unchanged by D4)

`Temperatures[]`: `Name` ("CPU1 Temp", "System Board Inlet Temp", "System Board
Exhaust Temp"), `ReadingCelsius`, `Status`. `Fans[]`: `Name` ("System Board
Fan1"…), `Reading` (RPM), `Status`. `PowerSupplies[]`: `Name` ("PS1 Status"…),
`Status`, plus OEM detail. `PowerControl[]`: `PowerConsumedWatts` (162 W on
this host at capture) → metric `power:consumed`.

## Mapping to the component model (DESIGN §5.2)

| Redfish resource | Component type | Name source | Health source |
|---|---|---|---|
| System | `system` | `System.Embedded.1` (synthetic) | `Status.Health` → `HealthRollup` fallback |
| Processor member | `cpu` | `Name` | `Status.Health` → `Status.State` |
| Memory member | `memory` | `Name` | `Status.Health` → `Status.State` |
| Storage member | `controller` | `Name` | `Status.Health` → `Status.State` |
| Drives member | `disk` | `Name` | `Status.Health` → `Status.State`, escalated by `FailurePredicted` |
| Temperature | `temp` | `Name` | `Status.Health` |
| Fan | `fan` | `Name` | `Status.Health` |
| Power supply | `psu` | `Name` | `Status.Health` |

Detail strings carry `Model`, `Manufacturer`, `SerialNumber`, `CapacityBytes`,
`FailurePredicted` and `HealthRollup` where present; `ReadingCelsius` per temp
and `PowerConsumedWatts` are stored as metrics.
