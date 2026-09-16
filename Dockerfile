FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Mosquito.Cloud.Api/Mosquito.Cloud.Api.csproj src/Mosquito.Cloud.Api/
RUN dotnet restore src/Mosquito.Cloud.Api/Mosquito.Cloud.Api.csproj
COPY src/Mosquito.Cloud.Api/ src/Mosquito.Cloud.Api/
RUN dotnet publish src/Mosquito.Cloud.Api/Mosquito.Cloud.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN useradd --system --uid 10001 --create-home mosquito-api
WORKDIR /app
COPY --from=build /app/ ./
USER 10001
EXPOSE 5080
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
ENTRYPOINT ["dotnet", "Mosquito.Cloud.Api.dll"]
