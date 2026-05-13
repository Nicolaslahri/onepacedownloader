"""DNS switcher for One Pace Downloader v2.

Detects the active Windows network interface, reads its current DNS config,
and switches it to Cloudflare (1.1.1.1 / 1.0.0.1) or back to DHCP via UAC-
elevated `netsh` invocations. Stdlib only — uses ctypes + subprocess.

All functions are safe to call from a Tk thread (they don't touch widgets).
"""

from __future__ import annotations

import ctypes
import re
import subprocess
import sys
import time

CLOUDFLARE_PRIMARY = "1.1.1.1"
CLOUDFLARE_SECONDARY = "1.0.0.1"
GOOGLE_PRIMARY = "8.8.8.8"
GOOGLE_SECONDARY = "8.8.4.4"

# subprocess.run flag that hides the console window on Windows
_CREATE_NO_WINDOW = 0x08000000


def _run_quiet(args: list[str]) -> tuple[int, str]:
    """Run a netsh-style command without flashing a console window. Returns
    (returncode, stdout). Stdout is decoded leniently in case Windows
    localizes the output."""
    try:
        p = subprocess.run(
            args,
            capture_output=True,
            creationflags=_CREATE_NO_WINDOW if sys.platform == "win32" else 0,
            timeout=15,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError) as e:
        return -1, f"{type(e).__name__}: {e}"
    out = (p.stdout or b"").decode("utf-8", errors="replace")
    if p.returncode != 0 and p.stderr:
        out += "\n" + p.stderr.decode("utf-8", errors="replace")
    return p.returncode, out


def detect_active_interface() -> str | None:
    """Return the name of the first Connected + Dedicated interface, or None.
    Matches what 'netsh interface show interface' lists."""
    rc, out = _run_quiet(["netsh", "interface", "show", "interface"])
    if rc != 0:
        return None
    # Columns: Admin State | State | Type | Interface Name
    for line in out.splitlines():
        line = line.strip()
        if not line or line.startswith(("Admin State", "---")):
            continue
        parts = re.split(r"\s{2,}", line, maxsplit=3)
        if len(parts) < 4:
            continue
        _admin, state, iface_type, name = parts
        if state.lower() == "connected" and iface_type.lower() == "dedicated":
            return name.strip()
    return None


def get_current_dns(iface: str) -> tuple[list[str], bool]:
    """Return (dns_ip_list, is_dhcp). is_dhcp=True means DNS is auto-assigned
    by DHCP rather than statically configured."""
    rc, out = _run_quiet(
        ["netsh", "interface", "ipv4", "show", "dnsservers", f"name={iface}"]
    )
    if rc != 0:
        return [], False
    is_dhcp = "dhcp" in out.lower()
    ips = re.findall(r"\b(?:\d{1,3}\.){3}\d{1,3}\b", out)
    # De-duplicate while preserving order
    seen: set[str] = set()
    uniq: list[str] = []
    for ip in ips:
        if ip not in seen:
            seen.add(ip)
            uniq.append(ip)
    return uniq, is_dhcp


def _shell_execute_elevated(cmdline: str) -> bool:
    """Launch cmd.exe elevated via UAC, run the supplied command line, return
    True only if the user accepted the UAC prompt (ShellExecuteW > 32)."""
    if sys.platform != "win32":
        return False
    # ShellExecuteW(hwnd, op, file, params, dir, nShowCmd)
    # /c runs the command then closes the window. We wrap in quotes so &&
    # operators work as one command line.
    h = ctypes.windll.shell32.ShellExecuteW(
        None, "runas", "cmd.exe", f'/c {cmdline}', None, 0
    )
    return int(h) > 32


def switch_to_cloudflare(iface: str) -> bool:
    """Configure the interface for Cloudflare DNS via UAC-elevated netsh.
    Returns True if the user accepted UAC (not whether DNS actually changed)."""
    return _switch_to_static(iface, CLOUDFLARE_PRIMARY, CLOUDFLARE_SECONDARY)


def switch_to_google(iface: str) -> bool:
    """Configure the interface for Google Public DNS — fallback for ISPs
    that also block Cloudflare (some Indian carriers do)."""
    return _switch_to_static(iface, GOOGLE_PRIMARY, GOOGLE_SECONDARY)


def _switch_to_static(iface: str, primary_ip: str, secondary_ip: str) -> bool:
    primary = (
        f'netsh interface ipv4 set dnsservers name="{iface}" '
        f"static {primary_ip} primary"
    )
    secondary = (
        f'netsh interface ipv4 add dnsservers name="{iface}" '
        f"{secondary_ip} index=2"
    )
    return _shell_execute_elevated(f'"{primary} && {secondary}"')


def revert_to_dhcp(iface: str) -> bool:
    """Reset the interface to DHCP DNS via UAC-elevated netsh."""
    cmd = f'netsh interface ipv4 set dnsservers name="{iface}" dhcp'
    return _shell_execute_elevated(f'"{cmd}"')


def wait_for_dns_change(iface: str, before: list[str], timeout_s: float = 5.0) -> list[str]:
    """Poll the interface until DNS differs from `before` or timeout. Returns
    the new DNS list (may equal `before` if the change never took effect)."""
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        time.sleep(0.4)
        now, _ = get_current_dns(iface)
        if now != before:
            return now
    return before


if __name__ == "__main__":
    # Quick CLI for development
    iface = detect_active_interface()
    print(f"Active interface: {iface!r}")
    if iface:
        ips, dhcp = get_current_dns(iface)
        print(f"  DNS: {ips} (dhcp={dhcp})")
