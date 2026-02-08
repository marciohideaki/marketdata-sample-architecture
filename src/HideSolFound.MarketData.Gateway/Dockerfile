FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy build configuration and restore
COPY Directory.Build.props .
COPY HideSolFound.MarketData.sln .
COPY src/HideSolFound.MarketData.Core/HideSolFound.MarketData.Core.csproj src/HideSolFound.MarketData.Core/
COPY src/HideSolFound.MarketData.Gateway/HideSolFound.MarketData.Gateway.csproj src/HideSolFound.MarketData.Gateway/

# Restore only gateway and its dependencies
RUN dotnet restore src/HideSolFound.MarketData.Gateway/HideSolFound.MarketData.Gateway.csproj

# Copy source files
COPY src/HideSolFound.MarketData.Core/ src/HideSolFound.MarketData.Core/
COPY src/HideSolFound.MarketData.Gateway/ src/HideSolFound.MarketData.Gateway/

# Build and publish
RUN dotnet publish src/HideSolFound.MarketData.Gateway/HideSolFound.MarketData.Gateway.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "HideSolFound.MarketData.Gateway.dll"]
