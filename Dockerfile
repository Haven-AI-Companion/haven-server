FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["haven-server-cs.csproj", "./"]
RUN dotnet restore "haven-server-cs.csproj"
COPY . .
RUN dotnet publish "haven-server-cs.csproj" -c Release -o /app/publish /p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 18799
COPY --from=build /app/publish .
ENTRYPOINT ["./haven-server-cs"]
