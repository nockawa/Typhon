import type {
  ArchetypeDto,
  ChunkManifestEntryDto,
  ComponentTypeDto,
  ProfilerHeaderDto,
  ProfilerMetadataDto,
  SystemDefinitionDto,
  TickSummaryDto,
} from '@/api/generated/model';
import type { ArchetypeDef, ChunkManifestEntry, ComponentTypeDef, SystemDef, TickSummary, TraceMetadata } from '@/libs/profiler/model/types';

/**
 * Convert the OpenAPI-generated `ProfilerMetadataDto` + `ChunkManifestEntryDto[]` shapes into the
 * internal `TraceMetadata` + `ChunkManifestEntry[]` shapes the chunk cache (and decoder) expect.
 *
 * Why this exists: orval types every integer field as `number | string` because the server emits
 * BigInt64-safe JSON for 64-bit values. The internal types are plain `number` — the cache path
 * math would be poisoned by a string coming through unconverted. This module does the coercion
 * in exactly one place so the rest of the client can ignore the split.
 */

const toInt = (v: number | string | null | undefined, fallback = 0): number => {
  if (v === null || v === undefined) return fallback;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : fallback;
};

const toFloat = toInt; // same conversion rule; alias for readability

export function convertHeader(dto: ProfilerHeaderDto): TraceMetadata['header'] {
  return {
    version: toInt(dto.version),
    timestampFrequency: toInt(dto.timestampFrequency),
    baseTickRate: toFloat(dto.baseTickRate),
    workerCount: toInt(dto.workerCount),
    systemCount: toInt(dto.systemCount),
    archetypeCount: toInt(dto.archetypeCount),
    componentTypeCount: toInt(dto.componentTypeCount),
    // DTO carries `createdUtcTicks` (100ns .NET ticks since epoch); internal model wants an ISO
    // string. Anchor at .NET's epoch (0001-01-01 UTC) so the conversion matches server output.
    createdUtc: ticksToIsoString(toInt(dto.createdUtcTicks)),
    samplingSessionStartQpc: toInt(dto.samplingSessionStartQpc),
  };
}

export function convertSystems(dtos: SystemDefinitionDto[] | null | undefined): SystemDef[] {
  if (!dtos) return [];
  return dtos.map((s) => ({
    index: toInt(s.index),
    name: s.name ?? '',
    type: toInt(s.type),
    priority: toInt(s.priority),
    isParallel: s.isParallel,
    tierFilter: toInt(s.tierFilter),
    predecessors: (s.predecessors ?? []).map((p) => toInt(p)),
    successors: (s.successors ?? []).map((p) => toInt(p)),
  }));
}

export function convertArchetypes(dtos: ArchetypeDto[] | null | undefined): ArchetypeDef[] {
  if (!dtos) return [];
  return dtos.map((a) => ({ archetypeId: toInt(a.archetypeId), name: a.name ?? '' }));
}

export function convertComponentTypes(dtos: ComponentTypeDto[] | null | undefined): ComponentTypeDef[] {
  if (!dtos) return [];
  return dtos.map((c) => ({ componentTypeId: toInt(c.componentTypeId), name: c.name ?? '' }));
}

export function convertTickSummaries(dtos: TickSummaryDto[] | null | undefined): TickSummary[] {
  if (!dtos) return [];
  return dtos.map((s) => ({
    tickNumber: toInt(s.tickNumber),
    startUs: toFloat(s.startUs),
    durationUs: toFloat(s.durationUs),
    eventCount: toInt(s.eventCount),
    maxSystemDurationUs: toFloat(s.maxSystemDurationUs),
    activeSystemsBitmask: s.activeSystemsBitmask ?? '0',
    // v9+ fields — Orval emits `number | string` for OpenAPI integer types per the .NET serialiser convention. Coerce
    // through toInt for parity with the other integer fields above. Pre-v9 traces emit 0 (server-side default).
    overloadLevel: toInt(s.overloadLevel ?? 0),
    tickMultiplier: toInt(s.tickMultiplier ?? 0),
    metronomeWaitUs: toInt(s.metronomeWaitUs ?? 0),
    metronomeIntentClass: toInt(s.metronomeIntentClass ?? 0),
    consecutiveOverrun: toInt(s.consecutiveOverrun ?? 0),
    consecutiveUnderrun: toInt(s.consecutiveUnderrun ?? 0),
  }));
}

export function convertChunkManifest(dtos: ChunkManifestEntryDto[] | null | undefined): ChunkManifestEntry[] {
  if (!dtos) return [];
  return dtos.map((c) => ({
    fromTick: toInt(c.fromTick),
    toTick: toInt(c.toTick),
    eventCount: toInt(c.eventCount),
    isContinuation: c.isContinuation,
  }));
}

/**
 * Convert a full `ProfilerMetadataDto` to the internal `TraceMetadata`. `threadNames` is not on
 * the DTO — it gets populated as `ThreadInfo` records decode from chunks and is accumulated on
 * this object by the chunk cache. Here we initialise it as empty so the cache has a mutable target.
 */
export function convertProfilerMetadata(dto: ProfilerMetadataDto): TraceMetadata {
  return {
    header: convertHeader(dto.header),
    systems: convertSystems(dto.systems),
    archetypes: convertArchetypes(dto.archetypes),
    componentTypes: convertComponentTypes(dto.componentTypes),
    threadNames: {},
  };
}

/**
 * Convert .NET ticks (100ns units since 0001-01-01 UTC) to an ISO-8601 string. Matches the
 * server-side `DateTime.UtcNow.Ticks` → `DateTime.ToString("O")` conversion so client and server
 * display identical timestamps.
 */
function ticksToIsoString(ticks: number): string {
  if (!Number.isFinite(ticks) || ticks <= 0) return '';
  // .NET ticks start at 0001-01-01 UTC; JS Date starts at 1970-01-01 UTC. The offset between the
  // two epochs is a fixed constant of 621_355_968_000_000_000 ticks (62135596800 seconds × 10M).
  const UNIX_EPOCH_TICKS = 621355968000000000;
  const unixMs = (ticks - UNIX_EPOCH_TICKS) / 10_000;
  if (!Number.isFinite(unixMs) || unixMs <= 0) return '';
  return new Date(unixMs).toISOString();
}
