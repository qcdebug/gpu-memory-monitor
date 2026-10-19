# GpuMon

Monitors GPU memory per process, live sensors, PCIe links and displays. Two separate programs:

## GpuMon.exe (Windows)

`dist\v1.0.1\GpuMon.exe`: one self-contained file. No installer, no container, no toolkit, no admin rights.

| Needs | Notes |
|---|---|
| Windows 10 1709 or later, x64 | the GPU memory counters need 1709+; Windows 7/8.1 are not supported |
| NVIDIA driver | optional; without it you keep per-process memory and engine load but lose the NVIDIA sensors |

Shows: VRAM and shared-memory use per process, engine load per process, sensors with history, PCIe link and
traffic, a VRAM-by-process history chart, and a Displays tab (arrangement, refresh rate measured from the vertical
blank, timings, EDID, colour depth). Crash reports are written to `Documents\GpuMon Logs`.

## container\ (Linux hosts)

A Docker/Podman image with a web dashboard and a Prometheus endpoint. Needs the NVIDIA driver and the
NVIDIA Container Toolkit on the host at run time (not to build the image). See `container\README.md`.

It is for Linux hosts. On a Windows host use `GpuMon.exe`, which can see Windows processes; a container on
Windows only gets sensors and the Windows PIDs of busy processes, without names or VRAM per process.

## Building

```
dotnet publish -c Release -o dist\v1.0.1                          # Windows app (needs the .NET 8 SDK)
docker build -f container/Dockerfile -t gpumon .                  # container (add --format docker with Podman)
```
