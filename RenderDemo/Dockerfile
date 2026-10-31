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
RUN dotnet publish TestProject.csproj -c Release --no-restore -o /out/app /p:UseAppHost=false

WORKDIR /src/RenderDemo/RateLimitProxy
COPY RenderDemo/RateLimitProxy/RateLimitProxy.csproj ./
RUN dotnet restore RateLimitProxy.csproj
COPY RenderDemo/RateLimitProxy/Program.cs ./
RUN dotnet publish RateLimitProxy.csproj -c Release --no-restore -o /out/proxy /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /out/app/ ./app/
COPY --from=build /out/proxy/ ./proxy/
COPY RenderDemo/DemoFiles/ /app/demo-files/
COPY RenderDemo/start-demo.sh /app/start-demo.sh
RUN chmod 0755 /app/start-demo.sh

ENV FileBrowser__RootPath=/app/demo-files
EXPOSE 10000
ENTRYPOINT ["/app/start-demo.sh"]
