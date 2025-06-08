# ������������ .NET SDK 8.0 ��� �����
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# ������� ��������� ���� � ���������� ���������
COPY *.csproj ./
RUN dotnet restore

# ������� ���� ����� � �������� ������ �����
COPY . ./
RUN dotnet publish -c Release -o out

# ������������� .NET ASP.NET Runtime 8.0
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# ��������� ���� ��������
ENTRYPOINT ["dotnet", "Telegram_bot.dll"]
