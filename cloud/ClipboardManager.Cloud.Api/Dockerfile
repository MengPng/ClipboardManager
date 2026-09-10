FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY publish/ .
USER $APP_UID
ENTRYPOINT ["dotnet", "ClipboardManager.Cloud.Api.dll"]
