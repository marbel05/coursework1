# Встановлюємо .NET SDK 8.0 для збірки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Копіюємо проектний файл і відновлюємо залежності
COPY *.csproj ./
RUN dotnet restore

# Копіюємо інші файли і публікуємо релізну збірку
COPY . ./
RUN dotnet publish -c Release -o out

# Використовуємо .NET ASP.NET Runtime 8.0
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# Запускаємо твою програму
ENTRYPOINT ["dotnet", "Telegram_bot.dll"]
