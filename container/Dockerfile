# Build from the repository root:
#   docker build -f container/Dockerfile -t gpumon .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# shared NVML code (also used by the Windows app) + the container project
COPY src/Model.cs src/NvmlReader.cs ./src/
COPY src/Native/Nvml.cs ./src/Native/
COPY container/ ./container/
RUN dotnet publish container/GpuMon.Container.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# the NVIDIA Container Toolkit injects libnvidia-ml.so.1 and the device nodes; "utility" is the capability NVML needs
ENV NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=utility \
    ASPNETCORE_HTTP_PORTS=9102 \
    GPUMON_PORT=9102 \
    DOTNET_EnableDiagnostics=0
WORKDIR /app
COPY --from=build /out .
EXPOSE 9102
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD ["dotnet", "gpumon.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "gpumon.dll"]
