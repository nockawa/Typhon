# Typhon Monitoring Demo

A visual demonstration of Typhon's observability features. This application:

1. **Bootstraps an Aspire Dashboard** container for real-time metrics visualization
2. **Connects Typhon's OTel metrics** to the dashboard
3. **Runs configurable load scenarios** to observe database behavior

## Quick Start

```bash
# From the Typhon repo root
dotnet run --project test/Typhon.MonitoringDemo/Typhon.MonitoringDemo.csproj
```

## Prerequisites

- **.NET 10.0 SDK**
- **Podman** or **Docker** (for running the Aspire Dashboard container)
- Container runtime must be running before starting the demo

## Features

### Automatic Dashboard Setup

The demo automatically:
- Detects your container runtime (Podman or Docker)
- Pulls the Aspire Dashboard image
- Starts a container with proper port mappings
- Waits for the dashboard to be ready
- Displays the URL for browser access

### Load Scenarios

#### Factory Game Scenarios
- **Factory Bootstrap**: Creates buildings, belts, resource nodes (heavy CREATE)
- **Factory Production**: Updates production progress, item quantities (heavy UPDATE)
- **Factory Supply Chain**: Simulates logistics, belt movement (mixed READ/UPDATE)

#### RPG Game Scenarios
- **RPG World Simulation**: NPC movement, player interactions (balanced CRUD)
- **RPG Combat**: Damage calculation, health updates (high-frequency UPDATE)
- **RPG Questing**: Quest progress, inventory rewards (mixed CRUD)

#### Advanced Scenarios
- **Mixed Workload**: Factory + RPG simultaneously (tests isolation)
- **High Contention**: Multiple workers on same entities (stress tests MVCC)

### Configurable Parameters

Each scenario can be configured with:
- **Duration**: 10 seconds to 5 minutes
- **Intensity**: Light (100 ops/s) to Stress (max throughput)
- **Concurrency**: 1 to 16 workers

## Observing Metrics

After running a scenario, open the Aspire Dashboard (default: `http://localhost:18888`):

1. Navigate to **Metrics** tab
2. Search for `typhon.resource`
3. Key metrics to watch:
   - `typhon.resource.storage.page_cache.capacity.utilization` - Cache pressure
   - `typhon.resource.storage.page_cache.throughput.cache_hits` - Cache effectiveness
   - `typhon.resource.database.transactions.throughput.commits` - Transaction rate
   - `typhon.resource.contention.*` - Lock wait behavior

## Architecture

```
┌──────────────────┐     ┌─────────────────────┐
│  MonitoringDemo  │────▶│  Typhon Engine      │
│  (Spectre.Console)     │  (DatabaseEngine)   │
└──────────────────┘     └─────────────────────┘
         │                         │
         │ OTLP/gRPC               │ IMetricSource
         ▼                         ▼
┌──────────────────┐     ┌─────────────────────┐
│ Aspire Dashboard │◀────│ ResourceMetricsExporter
│ (Container)      │     │ (OTel Meter)        │
└──────────────────┘     └─────────────────────┘
```

## ECS Components

The demo uses realistic game components:

### Factory Components
| Component | Purpose |
|-----------|---------|
| `FactoryBuilding` | Assemblers, furnaces, refineries |
| `ConveyorBelt` | Item transport between buildings |
| `ItemStack` | Stored items with quantity |
| `Recipe` | Production recipes with inputs/outputs |
| `ProductionQueue` | Queued production orders |
| `ResourceNode` | Mineable resource deposits |
| `PowerGrid` | Electricity production/consumption |

### RPG Components
| Component | Purpose |
|-----------|---------|
| `Character` | Players and NPCs with stats |
| `Inventory` | Items owned by characters |
| `Equipment` | Worn gear with bonuses |
| `Skill` | Learned abilities with cooldowns |
| `Quest` | Active/completed quests |
| `WorldPosition` | Entity location and movement |
| `CombatStats` | Attack, defense, kills/deaths |

## Troubleshooting

### Dashboard won't start
- Ensure Podman/Docker is running
- Check if ports 18888 and 4317 are available
- Try manually: `podman run -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:9.0`

### No metrics appearing
- Verify `typhon.telemetry.json` is in the output directory
- Check that telemetry is enabled in the config
- Ensure the scenario is generating load (check transaction count)

### Container runtime not found
- Ensure `podman` or `docker` is in your PATH
- On Windows, ensure the container service is running
