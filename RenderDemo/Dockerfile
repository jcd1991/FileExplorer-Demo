FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src/TestProject

COPY TestProject/TestProject.csproj ./
RUN dotnet restore TestProject.csproj

COPY TestProject/Program.cs ./
COPY TestProject/Controllers/ ./Controllers/
COPY TestProject/Models/ ./Models/
COPY TestProject/Properties/AssemblyInfo.cs ./Properties/AssemblyInfo.cs
COPY TestProject/Services/ ./Services/
COPY TestProject/wwwroot/ ./wwwroot/
COPY TestProject/appsettings.json ./
RUN dotnet publish TestProject.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /out/ ./
COPY RenderDemo/DemoFiles/ /app/demo-files/

ENV FileBrowser__RootPath=/app/demo-files
EXPOSE 10000
ENTRYPOINT ["sh", "-c", "exec dotnet TestProject.dll --urls http://0.0.0.0:${PORT:-10000}"]
