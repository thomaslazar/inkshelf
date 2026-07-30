FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Identifies non-release builds: "pr-34.a1b2c3d", "main.a1b2c3d". The SDK appends
# it to InformationalVersion as "+$BUILD_ID", which the libraries page shows.
# Empty (the default, and what release builds pass) leaves the version bare.
ARG BUILD_ID=
RUN dotnet publish src/Inkshelf/Inkshelf.csproj -c Release -o /app \
      ${BUILD_ID:+-p:SourceRevisionId=$BUILD_ID}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Inkshelf.dll"]
