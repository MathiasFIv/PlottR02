# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# copy csproj files first for layer-cached restore
COPY PlottR0.2.slnx ./
COPY src/PlottR0.2.Api/PlottR0.2.Api.csproj src/PlottR0.2.Api/
COPY src/PlottR0.2.Domain/PlottR0.2.Domain.csproj src/PlottR0.2.Domain/
COPY src/PlottR0.2.Contracts/PlottR0.2.Contracts.csproj src/PlottR0.2.Contracts/
COPY src/PlottR0.2.Services/PlottR0.2.Services.csproj src/PlottR0.2.Services/
COPY src/PlottR0.2.Repository/PlottR0.2.Repository.csproj src/PlottR0.2.Repository/
RUN dotnet restore src/PlottR0.2.Api/PlottR0.2.Api.csproj
COPY src/ src/
RUN dotnet publish src/PlottR0.2.Api/PlottR0.2.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
# Local write-through CACHE root: free tier has NO persistent disk, so this path lives only for
# the container's lifetime and is wiped on redeploy/restart/spin-down. Storage__Root points here;
# the durable copy of workbooks/recordings lives in Neon Object Storage.
RUN mkdir -p /var/data && chown -R app:app /var/data
USER app
# Render injects $PORT; ASP.NET Core does NOT read $PORT itself — bind explicitly:
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
CMD ["sh", "-c", "dotnet PlottR0.2.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
