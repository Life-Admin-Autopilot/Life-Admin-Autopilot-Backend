# The API, as one image.
#
# Multi-stage: the SDK builds, the ASP.NET runtime ships. The runtime image is
# ~110 MB against the SDK's ~850 MB, and nothing in production needs a compiler.
#
# DEBIAN, NOT ALPINE, and that is not a default — it is a requirement. This app
# resolves IANA zones by name (`TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo")`)
# on the path that decides what time a matter is filed at, and it formats Arabic
# dates through ICU. Alpine's .NET images ship without ICU unless `icu-libs` is
# added, and an ICU-less runtime does not throw at startup: it silently falls
# back to invariant culture, `FindSystemTimeZoneById` starts failing, and every
# clarification chip is composed in UTC. That is the +3-hour class of bug this
# codebase has already been through twice; it is not worth 40 MB.

# ---- build ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The csproj files ALONE first, so `restore` lands in its own layer. Docker
# reuses it on every build that did not change a package reference, which is
# almost all of them — copying the whole tree up front would re-restore on every
# source edit and turn a 20-second build into a three-minute one.
COPY Life-Admin-Autopilot-Backend/Life-Admin-Autopilot.PL.csproj Life-Admin-Autopilot-Backend/
COPY Life-Admin-Autopilot.BLL/Life-Admin-Autopilot.BLL.csproj    Life-Admin-Autopilot.BLL/
COPY Life-Admin-Autopilot.DAL/Life-Admin-Autopilot.DAL.csproj    Life-Admin-Autopilot.DAL/
RUN dotnet restore Life-Admin-Autopilot-Backend/Life-Admin-Autopilot.PL.csproj

# The test project is deliberately not restored or copied. It references PL
# transitively, so including it would pull xunit and the whole test graph into
# an image that will never run a test.
COPY Life-Admin-Autopilot-Backend/ Life-Admin-Autopilot-Backend/
COPY Life-Admin-Autopilot.BLL/     Life-Admin-Autopilot.BLL/
COPY Life-Admin-Autopilot.DAL/     Life-Admin-Autopilot.DAL/

RUN dotnet publish Life-Admin-Autopilot-Backend/Life-Admin-Autopilot.PL.csproj \
    -c Release \
    -o /app \
    --no-restore \
    /p:UseAppHost=false

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root. The base image ships an `app` user (uid 1654) for exactly this, and
# nothing here writes to disk: uploads go to Azure Blob, and the identity
# database is MonsterASP over the network.
USER app

COPY --from=build --chown=app:app /app .

# The same port as dev, so every URL in .env.example carries over unchanged.
#
# [::] rather than 0.0.0.0: it binds dual-stack, which is what `up.sh` uses
# locally and what Container Apps' probes reach the container on. Binding IPv4
# only works until something asks for ::1 and then fails as a connection
# refused, which reads like the app being down.
ENV ASPNETCORE_URLS=http://[::]:4000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 4000

# No HEALTHCHECK instruction. Container Apps runs its own probes from the
# platform and ignores the image's, so one here would be dead weight that also
# disagrees with the real definition of healthy the moment either changes.
# `/health` is the endpoint; it is configured on the container app.

ENTRYPOINT ["dotnet", "Life-Admin-Autopilot.PL.dll"]
