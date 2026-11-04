# .NET API (ASP.NET Core). Build context: the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY shared/ ./shared/
COPY api-dotnet/ ./api-dotnet/
RUN dotnet publish api-dotnet/PitchWise.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is for the Compose healthcheck; the base image has neither curl nor wget.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "PitchWise.Api.dll"]
