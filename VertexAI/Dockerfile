# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

COPY VertexAI.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime

# SkiaSharp 需要 libfontconfig 和 libuuid
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 libuuid1 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8880

EXPOSE 8880

ENTRYPOINT ["dotnet", "VertexAI.dll"]
