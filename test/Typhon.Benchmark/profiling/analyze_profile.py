#!/usr/bin/env python3
"""
Typhon dotTrace Profile Analyzer
Parses dotTrace Reporter XML output and produces actionable performance reports.

Usage:
    python analyze_profile.py report.xml                    # Single profile analysis
    python analyze_profile.py --compare base.xml current.xml # Compare two profiles
    python analyze_profile.py report.xml --top 20           # Show top 20 functions
    python analyze_profile.py report.xml --filter BTree     # Filter by pattern
    python analyze_profile.py report.xml --op Remove        # Breakdown for specific operation
    python analyze_profile.py report.xml --json             # Output as JSON
"""

import xml.etree.ElementTree as ET
import argparse
import json
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field


# --- Data Model -------------------------------------------------------

@dataclass
class CallInstance:
    call_stack: list[str]
    total_time: float
    own_time: float
    samples: int


@dataclass
class Function:
    id: str
    fqn: str
    total_time: float
    own_time: float
    samples: int
    instances: list[CallInstance] = field(default_factory=list)

    @property
    def short_name(self) -> str:
        """Extract short method name from FQN like 'Namespace.Class.Method(args)' -> 'Class.Method'
        For constructors (.ctor), include the class name: 'Class..ctor'
        For nested types (Class+Inner), preserve the nesting: 'Inner.Method'"""
        # Remove parameter list for display
        base = self.fqn.split("(")[0]
        # Handle nested types: A+B.Method -> B.Method
        if "+" in base:
            # Take from last '+' onwards
            nested = base[base.rfind("+") + 1:]
            parts = nested.rsplit(".", 1)
            if len(parts) == 2:
                return f"{parts[0]}.{parts[1]}"
            return nested
        parts = base.rsplit(".", 2)
        if len(parts) >= 2:
            return f"{parts[-2]}.{parts[-1]}"
        return base

    @property
    def method_name(self) -> str:
        """Just the method name"""
        base = self.fqn.split("(")[0]
        return base.rsplit(".", 1)[-1]


@dataclass
class Profile:
    functions: list[Function]
    total_time: float = 0.0

    def __post_init__(self):
        # Compute total time from the root function (highest TotalTime)
        if self.functions:
            self.total_time = max(f.total_time for f in self.functions)


# --- Parsing ----------------------------------------------------------

def parse_report(xml_path: str) -> Profile:
    """Parse a dotTrace Reporter XML file into a Profile."""
    tree = ET.parse(xml_path)
    root = tree.getroot()

    functions = []
    for func_elem in root.findall("Function"):
        instances = []
        for inst_elem in func_elem.findall("Instance"):
            stack_str = inst_elem.get("CallStack", "")
            stack = stack_str.split("/") if stack_str else []
            instances.append(CallInstance(
                call_stack=stack,
                total_time=float(inst_elem.get("TotalTime", 0)),
                own_time=float(inst_elem.get("OwnTime", 0)),
                samples=int(inst_elem.get("Samples", 0)),
            ))

        functions.append(Function(
            id=func_elem.get("Id", ""),
            fqn=func_elem.get("FQN", ""),
            total_time=float(func_elem.get("TotalTime", 0)),
            own_time=float(func_elem.get("OwnTime", 0)),
            samples=int(func_elem.get("Samples", 0)),
            instances=instances,
        ))

    return Profile(functions=functions)


# --- Analysis ---------------------------------------------------------

def top_by_own_time(profile: Profile, n: int = 15, filter_pattern: str = None) -> list[Function]:
    """Top N functions by self/own time (where CPU time is actually spent)."""
    funcs = profile.functions
    if filter_pattern:
        funcs = [f for f in funcs if re.search(filter_pattern, f.fqn, re.IGNORECASE)]
    return sorted(funcs, key=lambda f: f.own_time, reverse=True)[:n]


def top_by_total_time(profile: Profile, n: int = 15, filter_pattern: str = None) -> list[Function]:
    """Top N functions by inclusive/total time (most expensive call trees)."""
    funcs = profile.functions
    if filter_pattern:
        funcs = [f for f in funcs if re.search(filter_pattern, f.fqn, re.IGNORECASE)]
    return sorted(funcs, key=lambda f: f.total_time, reverse=True)[:n]


def operation_breakdown(profile: Profile) -> dict[str, dict]:
    """Break down time by top-level BTree operation (Add, Remove, TryGet, etc.)."""
    ops = defaultdict(lambda: {"total_time": 0.0, "own_time": 0.0, "samples": 0, "children": defaultdict(float)})

    # Identify operation from call stack: look for BTree`1.Add, BTree`1.Remove, BTree`1.TryGet
    op_patterns = {
        "Add (Insert)": re.compile(r"BTree`1\.Add\("),
        "Remove (Delete)": re.compile(r"BTree`1\.Remove\("),
        "TryGet (Lookup)": re.compile(r"BTree`1\.TryGet\("),
        "RemoveValue": re.compile(r"BTree`1\.RemoveValue\("),
    }

    for func in profile.functions:
        for inst in func.instances:
            # Find which operation this call stack belongs to
            matched_op = None
            for op_name, pattern in op_patterns.items():
                for frame in inst.call_stack:
                    if pattern.search(frame):
                        matched_op = op_name
                        break
                if matched_op:
                    break

            if matched_op:
                ops[matched_op]["own_time"] += inst.own_time
                ops[matched_op]["samples"] += inst.samples
                # Attribute own_time to the leaf function
                if inst.own_time > 0:
                    ops[matched_op]["children"][func.short_name] += inst.own_time

    # Set total_time from the top-level operation functions
    for func in profile.functions:
        for op_name, pattern in op_patterns.items():
            if pattern.search(func.fqn) and op_name in ops:
                ops[op_name]["total_time"] = max(ops[op_name]["total_time"], func.total_time)

    return dict(ops)


def callers_of(profile: Profile, method_pattern: str) -> list[tuple[str, float, int]]:
    """Find all callers of a function matching the pattern, with time attribution."""
    callers = defaultdict(lambda: {"time": 0.0, "samples": 0})

    for func in profile.functions:
        if not re.search(method_pattern, func.fqn, re.IGNORECASE):
            continue
        for inst in func.instances:
            # The caller is the second-to-last frame in the call stack
            if len(inst.call_stack) >= 2:
                caller = inst.call_stack[-2]
                # Shorten caller name
                caller_short = caller.split("(")[0].rsplit(".", 2)
                if len(caller_short) >= 2:
                    caller_name = f"{caller_short[-2]}.{caller_short[-1]}"
                else:
                    caller_name = caller
                callers[caller_name]["time"] += inst.total_time
                callers[caller_name]["samples"] += inst.samples

    return sorted(callers.items(), key=lambda x: x[1]["time"], reverse=True)


# --- Comparison -------------------------------------------------------

def compare_profiles(base: Profile, current: Profile, n: int = 15) -> list[dict]:
    """Compare two profiles and show regressions/improvements."""
    base_map = {f.fqn: f for f in base.functions}
    current_map = {f.fqn: f for f in current.functions}

    all_fqns = set(base_map.keys()) | set(current_map.keys())
    results = []

    for fqn in all_fqns:
        b = base_map.get(fqn)
        c = current_map.get(fqn)

        base_own = b.own_time if b else 0
        curr_own = c.own_time if c else 0
        base_total = b.total_time if b else 0
        curr_total = c.total_time if c else 0

        delta_own = curr_own - base_own
        delta_total = curr_total - base_total

        short = (c or b).short_name

        results.append({
            "fqn": fqn,
            "short_name": short,
            "base_own": base_own,
            "curr_own": curr_own,
            "delta_own": delta_own,
            "pct_own": (delta_own / base_own * 100) if base_own > 0 else float('inf') if curr_own > 0 else 0,
            "base_total": base_total,
            "curr_total": curr_total,
            "delta_total": delta_total,
        })

    # Sort by absolute delta in own time (biggest changes first)
    results.sort(key=lambda r: abs(r["delta_own"]), reverse=True)
    return results[:n]


# --- Formatting -------------------------------------------------------

def format_time(ms: float) -> str:
    """Format milliseconds with appropriate precision."""
    if ms >= 100:
        return f"{ms:.0f}ms"
    elif ms >= 10:
        return f"{ms:.1f}ms"
    elif ms >= 1:
        return f"{ms:.2f}ms"
    else:
        return f"{ms * 1000:.0f}us"


def safe_print(*args, **kwargs):
    """Print with fallback for Windows console encoding issues."""
    try:
        print(*args, **kwargs)
    except UnicodeEncodeError:
        text = str(args[0]) if args else ""
        safe = text.encode(sys.stdout.encoding or "utf-8", errors="replace").decode(sys.stdout.encoding or "utf-8", errors="replace")
        print(safe, **{k: v for k, v in kwargs.items() if k != "end"})


def format_pct(value: float, total: float) -> str:
    """Format as percentage of total."""
    if total <= 0:
        return "  -  "
    pct = value / total * 100
    if pct >= 10:
        return f"{pct:5.1f}%"
    elif pct >= 1:
        return f"{pct:5.2f}%"
    else:
        return f"{pct:5.2f}%"


def print_header(title: str):
    print()
    print(f"{'=' * 80}")
    print(f"  {title}")
    print(f"{'=' * 80}")


def print_top_functions(funcs: list[Function], title: str, total_time: float, by_own: bool = True):
    print_header(title)
    print()

    time_col = "OwnTime" if by_own else "TotalTime"
    name_width = 55

    print(f"  {'#':>3}  {'Function':<{name_width}}  {time_col:>8}  {'%':>6}  {'Samples':>7}")
    print(f"  {'-' * 3}  {'-' * name_width}  {'-' * 8}  {'-' * 6}  {'-' * 7}")

    for i, f in enumerate(funcs, 1):
        time_val = f.own_time if by_own else f.total_time
        name = f.short_name
        if len(name) > name_width:
            name = name[:name_width - 3] + "..."
        print(f"  {i:3}  {name:<{name_width}}  {format_time(time_val):>8}  {format_pct(time_val, total_time):>6}  {f.samples:>7}")

    print()


def print_operation_breakdown(ops: dict[str, dict]):
    print_header("Operation Breakdown (time by BTree operation)")
    print()

    for op_name, data in sorted(ops.items(), key=lambda x: x[1].get("total_time", 0), reverse=True):
        total = data["total_time"]
        own_sum = data["own_time"]
        samples = data["samples"]

        print(f"  {op_name}")
        print(f"    Total: {format_time(total):>8}  |  Self sum: {format_time(own_sum):>8}  |  Samples: {samples}")

        # Top children by own time
        children = sorted(data["children"].items(), key=lambda x: x[1], reverse=True)
        if children:
            print(f"    {'Hot spots':}")
            for child_name, child_time in children[:8]:
                bar_len = int(child_time / max(c[1] for c in children) * 20) if children[0][1] > 0 else 0
                bar = "#" * bar_len
                print(f"      {format_time(child_time):>8}  {bar:<20}  {child_name}")
        print()


def print_comparison(results: list[dict]):
    print_header("Profile Comparison (base -> current)")
    print()

    name_width = 45
    print(f"  {'Function':<{name_width}}  {'Base':>8}  {'Curr':>8}  {'Delta':>8}  {'Change':>8}")
    print(f"  {'-' * name_width}  {'-' * 8}  {'-' * 8}  {'-' * 8}  {'-' * 8}")

    for r in results:
        name = r["short_name"]
        if len(name) > name_width:
            name = name[:name_width - 3] + "..."

        delta = r["delta_own"]
        if delta > 0:
            change = f"+{r['pct_own']:.0f}%" if r['pct_own'] != float('inf') else "  NEW"
            indicator = "^"  # regression
        elif delta < 0:
            change = f"{r['pct_own']:.0f}%"
            indicator = "v"  # improvement
        else:
            change = "  ="
            indicator = " "

        print(f"  {name:<{name_width}}  {format_time(r['base_own']):>8}  {format_time(r['curr_own']):>8}  {format_time(abs(delta)):>8}  {indicator}{change:>7}")

    print()


def print_callers(callers: list[tuple[str, dict]], method: str):
    print_header(f"Callers of *{method}*")
    print()

    name_width = 55
    print(f"  {'Caller':<{name_width}}  {'Time':>8}  {'Samples':>7}")
    print(f"  {'-' * name_width}  {'-' * 8}  {'-' * 7}")

    for caller_name, data in callers:
        name = caller_name
        if len(name) > name_width:
            name = name[:name_width - 3] + "..."
        print(f"  {name:<{name_width}}  {format_time(data['time']):>8}  {data['samples']:>7}")

    print()


# --- JSON Output ------------------------------------------------------

def profile_to_json(profile: Profile, n: int = 30, filter_pattern: str = None) -> dict:
    """Export profile analysis as JSON for programmatic consumption."""
    top_own = top_by_own_time(profile, n, filter_pattern)
    top_total = top_by_total_time(profile, n, filter_pattern)
    ops = operation_breakdown(profile)

    return {
        "total_time_ms": profile.total_time,
        "function_count": len(profile.functions),
        "top_by_own_time": [
            {"fqn": f.fqn, "short": f.short_name, "own_time": f.own_time, "total_time": f.total_time, "samples": f.samples}
            for f in top_own
        ],
        "top_by_total_time": [
            {"fqn": f.fqn, "short": f.short_name, "own_time": f.own_time, "total_time": f.total_time, "samples": f.samples}
            for f in top_total
        ],
        "operation_breakdown": {
            op: {
                "total_time": data["total_time"],
                "own_time_sum": data["own_time"],
                "samples": data["samples"],
                "hot_spots": sorted(data["children"].items(), key=lambda x: x[1], reverse=True)[:10],
            }
            for op, data in ops.items()
        },
    }


# --- Main -------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="Typhon dotTrace Profile Analyzer")
    parser.add_argument("report", help="Path to dotTrace Reporter XML file")
    parser.add_argument("--compare", metavar="CURRENT", help="Compare with a second profile XML")
    parser.add_argument("--top", type=int, default=15, help="Number of top functions to show (default: 15)")
    parser.add_argument("--filter", help="Regex pattern to filter functions (e.g., 'BTree', 'Remove')")
    parser.add_argument("--op", help="Show detailed breakdown for a specific operation (e.g., 'Remove')")
    parser.add_argument("--callers", help="Show callers of functions matching pattern")
    parser.add_argument("--json", action="store_true", help="Output as JSON")
    args = parser.parse_args()

    profile = parse_report(args.report)

    if args.json:
        result = profile_to_json(profile, args.top, args.filter)
        if args.compare:
            current = parse_report(args.compare)
            result["comparison"] = compare_profiles(profile, current, args.top)
        print(json.dumps(result, indent=2, default=str))
        return

    if args.compare:
        current = parse_report(args.compare)
        results = compare_profiles(profile, current, args.top)
        print_comparison(results)
        return

    if args.callers:
        callers = callers_of(profile, args.callers)
        print_callers(callers, args.callers)
        return

    # Default: full analysis
    print(f"\n  Profile: {args.report}")
    print(f"  Functions: {len(profile.functions)}  |  Total time: {format_time(profile.total_time)}")

    print_top_functions(
        top_by_own_time(profile, args.top, args.filter),
        "Top Functions by Self Time (where CPU time is spent)",
        profile.total_time,
        by_own=True,
    )

    print_top_functions(
        top_by_total_time(profile, args.top, args.filter),
        "Top Functions by Inclusive Time (most expensive call trees)",
        profile.total_time,
        by_own=False,
    )

    ops = operation_breakdown(profile)
    if ops:
        print_operation_breakdown(ops)

    if args.op:
        # Show callers for the specific operation pattern
        callers = callers_of(profile, args.op)
        if callers:
            print_callers(callers, args.op)


if __name__ == "__main__":
    main()
