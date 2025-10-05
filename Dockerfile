# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY TripleTriadApi.csproj .
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Copy published app
COPY --from=build /app/publish .

# Expose port (Koyeb uses port 8000 internally)
EXPOSE 8000

# Set environment to Production
ENV ASPNETCORE_ENVIRONMENT=Production

# Force app to listen on port 8000 (override Koyeb's PORT env var)
ENV ASPNETCORE_URLS=http://+:8000

ENTRYPOINT ["dotnet", "TripleTriadApi.dll"]
