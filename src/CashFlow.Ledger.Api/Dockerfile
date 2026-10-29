# Build: docker build -f src/CashFlow.Ledger.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY CashFlow.sln ./
COPY src/CashFlow.Shared/CashFlow.Shared.csproj src/CashFlow.Shared/
COPY src/CashFlow.Ledger.Api/CashFlow.Ledger.Api.csproj src/CashFlow.Ledger.Api/
RUN dotnet restore src/CashFlow.Ledger.Api/CashFlow.Ledger.Api.csproj

COPY src/ src/
RUN dotnet publish src/CashFlow.Ledger.Api/CashFlow.Ledger.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CashFlow.Ledger.Api.dll"]
