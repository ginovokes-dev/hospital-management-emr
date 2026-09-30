# Danphe EMR for Docker (Linux, and therefore Mac / Windows through Docker Desktop, or any server).
#
# The application is ASP.NET Core 2.0 compiled for the .NET Framework 4.6.1. Here it is compiled against Mono's copy of the 4.6.1
# reference assemblies and run with Mono, so no Windows machine is needed. Three steps: front-end, back-end, run.
#
#   docker build -f docker/Dockerfile -t danphe-emr .        (docker compose does this for you)

############################################################################################################################
# 1. Front-end: the Angular 7 application needs Node 12
############################################################################################################################
FROM node:12-buster-slim AS web
# same folder layout as in the repository: some source files import scripts / styles that live beside the app (see the two COPY lines below)
WORKDIR /web/wwwroot/DanpheApp
COPY Code/Websites/DanpheEMR/wwwroot/DanpheApp/package.json ./package.json
# the exact package versions this front-end was built and tested with
COPY docker/build-support/angular-package-lock.json ./package-lock.json
COPY docker/build-support/with-extra-ca.sh /usr/local/bin/with-extra-ca
COPY docker/build-support/ca/ /extra-ca/
RUN with-extra-ca npm ci --ignore-scripts --no-audit --no-fund
COPY Code/Websites/DanpheEMR/wwwroot/assets-dph/dicom-assets/ /web/wwwroot/assets-dph/dicom-assets/
# ... and three components import one stylesheet from ../themes
COPY Code/Websites/DanpheEMR/wwwroot/themes/theme-default/loading.component.css /web/wwwroot/themes/theme-default/loading.component.css
COPY Code/Websites/DanpheEMR/wwwroot/DanpheApp/ ./
# the build was measured to fit in 3 GB of memory; 3.5 GB is allowed for the JavaScript heap
RUN node --max_old_space_size=3584 node_modules/@angular/cli/bin/ng build

############################################################################################################################
# 2. Back-end: compile the .NET Framework 4.6.1 projects
############################################################################################################################
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# Mono's reference assemblies stand in for the Windows-only .NET Framework 4.6.1 while compiling
COPY --from=mono:6.12 /usr/lib/mono/4.6.1-api /usr/lib/mono/4.6.1-api
COPY --from=mono:6.12 /usr/lib/mono/xbuild-frameworks /usr/lib/mono/xbuild-frameworks
WORKDIR /src
COPY Code/ Code/
COPY Database/ Database/
COPY docker/build-support/ /build-support/
COPY docker/build-support/ca/ /extra-ca/
COPY docker/build-support/Directory.Build.targets Code/Directory.Build.targets
RUN rm -rf Code/Websites/DanpheEMR/wwwroot/DanpheApp
# the older component projects list their NuGet packages in packages.config: fetch them the way "nuget restore" would
RUN /build-support/with-extra-ca.sh bash -c 'bash /build-support/restore-packages-config.sh Code \
 && bash /build-support/make-refs.sh /src \
 && cd Code/Websites/DanpheEMR \
 && { [ -e App.config ] || ln -s app.config App.config; } \
 && dotnet build DanpheEMR.csproj -c Debug -v:minimal \
      -p:TargetFrameworkRootPath=/usr/lib/mono/xbuild-frameworks/ -p:FrameworkPathOverride=/refs/4.6.1-api'
# what the running site needs (the Angular files are added in the next stage)
RUN mkdir -p /out/native \
 && cd Code/Websites/DanpheEMR \
 && tar --exclude=./obj --exclude=./.vs -cf - . | tar -xf - -C /out \
 && cp /root/.nuget/packages/libuv/1.10.0/runtimes/linux-x64/native/libuv.so /out/native/libuv.so

############################################################################################################################
# 3. Run
############################################################################################################################
FROM mono:6.12 AS run
WORKDIR /app
COPY --from=api /out/ /app/
COPY --from=web /web/wwwroot/DanpheApp/dist/DanpheApp/ /app/wwwroot/DanpheApp/dist/DanpheApp/
COPY docker/entrypoint.sh docker/fix-mono.sh docker/encrypt-for-danphe.sh /app/docker/
RUN chmod +x /app/docker/*.sh \
 && /app/docker/fix-mono.sh /app/bin/Debug/net461 \
 # Razor compiles the pages when the application starts and needs the framework's reference assemblies in one flat folder
 && mkdir -p /opt/refs/flat-461 \
 && for f in /usr/lib/mono/4.6.1-api/Facades/*.dll /usr/lib/mono/4.6.1-api/*.dll; do ln -sf "$f" /opt/refs/flat-461/; done \
 && for f in /usr/lib/mono/4.5/Facades/*.dll; do b=$(basename "$f"); [ -e "/opt/refs/flat-461/$b" ] || ln -s "$f" "/opt/refs/flat-461/$b"; done
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=10s --start-period=180s --retries=10 \
  CMD curl -fsS -o /dev/null http://127.0.0.1:8080/Account/Login || exit 1
ENTRYPOINT ["/app/docker/entrypoint.sh"]
