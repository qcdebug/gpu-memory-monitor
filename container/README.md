# GpuMon container

A web dashboard and Prometheus exporter for NVIDIA GPUs, packaged as a Docker image. It uses the same NVML
reading code as the Windows app (`..\src`), so the sensors match: load, memory-controller load, VRAM, PCIe
link and traffic, clocks, temperatures, power, throttle reasons. It also lists the processes using each GPU.

## Requirements

On the host that runs the container (Linux):

| Needed | For | When |
|---|---|---|
| NVIDIA driver | the GPUs themselves and `libnvidia-ml.so.1` | always |
| Docker or Podman | running the image | always |
| [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/) | passes the GPUs and the driver libraries into the container (`--gpus all`) | **every time the container runs**; not needed to build the image |
| `--pid=host` | process names and command lines | optional |
| `/var/run/docker.sock` mounted read-only | container names for each process | optional |

Without the toolkit the container starts but sees no GPUs, and the log says NVML was not found.
Nothing in the image bundles NVIDIA files, so it always matches the host's driver.

## Run

Requires the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/) on the host.

```sh
docker build -f container/Dockerfile -t gpumon .          # from the repository root

docker run -d --name gpumon --restart unless-stopped \
  --gpus all --pid=host -p 9102:9102 \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  gpumon
```

or `docker compose -f container/docker-compose.yml up -d --build`.

Open `http://<host>:9102/`. Prometheus scrapes `http://<host>:9102/metrics`; JSON is at `/api/snapshot`.

| Option | Why |
|---|---|
| `--gpus all` | gives the container the GPUs and the host's `libnvidia-ml.so.1` |
| `--pid=host` | lets the container read `/proc` for host PIDs, so processes get names and command lines |
| `docker.sock` (read-only) | optional; maps each process to its container name instead of a 12-character id |

The socket is only used to list container names (`GET /containers/json`). Leave it out if you would rather not
mount it; everything else works.

Environment: `GPUMON_PORT` (default 9102), `GPUMON_INTERVAL_MS` (default 1000), `DOCKER_SOCKET`.

## What you get depends on the host

| Host | Sensors | Per-process VRAM | Per-process load | Process names |
|---|---|---|---|---|
| Linux + NVIDIA driver | yes | yes | yes | yes (with `--pid=host`) |
| Windows via Docker Desktop / WSL2 | yes | **no** | yes, Windows PIDs | **no** |

On a Windows host the Linux VM sees the GPUs and every sensor, and it sees the Windows PID and SM/memory load of
busy processes, but the driver does not report their VRAM and Windows processes are not in the VM's `/proc`.
For VRAM per Windows process use the Windows app (`GpuMon.exe`), which reads the Windows GPU counters.

The Windows-only features (displays, engine breakdown, shared-memory spill) are not in the container.

## Metrics

`gpumon_memory_used_bytes`, `gpumon_memory_total_bytes`, `gpumon_utilization_gpu_percent`,
`gpumon_utilization_memory_percent`, `gpumon_temperature_celsius`, `gpumon_power_watts`, `gpumon_clock_*_mhz`,
`gpumon_pcie_rx_bytes_per_second`, `gpumon_pcie_tx_bytes_per_second`, `gpumon_pcie_link_generation`,
`gpumon_pcie_link_width`, `gpumon_pcie_link_utilization_percent`, `gpumon_pcie_replays`,
`gpumon_process_memory_used_bytes{gpu,pid,name,container}` and `gpumon_up`.

Example alert: a card that negotiated fewer lanes than it supports.

```
gpumon_pcie_link_width < gpumon_pcie_link_width_max
```

Link generation drops at idle to save power, so alert on width, or on generation only while the GPU is busy.

## Status

Tested (Podman 5.8 on Windows 10 + WSL2, two RTX 4090s): the image builds, the container starts, NVML loads inside
it and sees both GPUs, the sensors, `/api/snapshot`, `/metrics`, `/healthz` and the web page all work, and the
healthcheck passes. GPU access there was the WSL2 passthrough (`--device /dev/dxg -v /usr/lib/wsl:/usr/lib/wsl:ro
-e LD_LIBRARY_PATH=/usr/lib/wsl/lib`), not `--gpus all`.

Not tested: `--gpus all` with the NVIDIA Container Toolkit on a real Linux host, per-process VRAM and process
names with `--pid=host` there, and the `docker.sock` container-name lookup.

Podman note: build with `podman build --format docker ...` or the HEALTHCHECK is dropped (OCI format has none).
