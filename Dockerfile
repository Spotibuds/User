FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["User.csproj", "."]
RUN dotnet restore "User.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "User.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "User.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "User.dll"] 