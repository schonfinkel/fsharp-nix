{
  description = "F# Development Environment";

  inputs = {
    nixpkgs.url = "https://channels.nixos.org/nixos-unstable/nixexprs.tar.zst";

    devenv = {
      url = "github:cachix/devenv";
      inputs.nixpkgs.follows = "nixpkgs";
    };

    flake-parts = {
      url = "github:hercules-ci/flake-parts";
    };

    treefmt-nix.url = "github:numtide/treefmt-nix";
  };

  outputs =
    inputs@{
      self,
      devenv,
      flake-parts,
      nixpkgs,
      ...
    }:
    flake-parts.lib.mkFlake { inherit inputs; } {
      imports = [
        inputs.devenv.flakeModule
        inputs.treefmt-nix.flakeModule
      ];
      systems = nixpkgs.lib.systems.flakeExposed;

      perSystem =
        {
          config,
          self',
          inputs',
          pkgs,
          lib,
          system,
          ...
        }:
        let
          app_name = "fsnix";
          net10 = pkgs.dotnet-sdk_10;
          version = "1.0.0";

          # Shared libraries the QuestPDF native binaries (libQuestPdfSkia.so, libqpdf.so) need
          # beyond glibc, from `patchelf --print-needed` on the linux-x64/arm64 runtimes of
          # QuestPDF 2026.9.1. Re-check on every QuestPDF bump. No fontconfig/freetype: fonts are
          # the Lato family QuestPDF ships beside the app, never system fonts.
          questpdfLibs = lib.optionals pkgs.stdenv.hostPlatform.isLinux [
            pkgs.stdenv.cc.cc.lib
            pkgs.zlib
          ];

          app = pkgs.buildDotnetModule {
            pname = app_name;
            inherit version;
            src = pkgs.lib.cleanSourceWith {
              src = ./.;
              filter =
                path: type:
                let
                  name = baseNameOf path;
                in
                !(
                  type == "directory"
                  && builtins.elem name [
                    ".config"
                    "out"
                  ]
                );
            };
            projectFile = "src/App/App.fsproj";
            nugetDeps = ./deps.json;
            dotnet-sdk = net10;
            dotnet-runtime = pkgs.dotnet-aspnetcore_10;
            executables = [ "App" ];
            doCheck = false;

            # Gives the prebuilt QuestPDF .so files a Nix rpath; fails the build if a needed
            # library is unresolved instead of failing at runtime with DllNotFoundException.
            nativeBuildInputs = lib.optionals pkgs.stdenv.hostPlatform.isLinux [ pkgs.autoPatchelfHook ];
            buildInputs = questpdfLibs;
          };
        in
        {
          # This sets `pkgs` to a nixpkgs with allowUnfree option set.
          _module.args.pkgs = import nixpkgs {
            inherit system;
            config.allowUnfree = true;
          };

          packages = {
            default = app;

            oci = pkgs.dockerTools.buildLayeredImage {
              name = app_name;
              tag = version;
              contents = [ pkgs.cacert ];
              # A writable, sticky /tmp: .NET and QuestPDF (TemporaryStoragePath) need one, and the
              # image otherwise has none. The Nix sandbox always provides a TMPDIR, so only
              # `docker run ... --render-check` against this image exercises it.
              extraCommands = ''
                mkdir -m 1777 tmp
              '';
              config = {
                Entrypoint = [ "${app}/bin/App" ];
                User = "65532:65532";
                ExposedPorts."8080/tcp" = { };
                Env = [
                  "ASPNETCORE_URLS=http://0.0.0.0:8080"
                  "DOTNET_EnableDiagnostics=0"
                ];
              };
            };
          };

          checks = {
            application = app;

            # OCI font check: the build sandbox, like the image, has no /etc/fonts and no
            # fontconfig, so this proves the patched native library and the shipped fonts load.
            pdf-render = pkgs.runCommand "${app_name}-pdf-render" { } ''
              export HOME=$TMPDIR
              ${app}/bin/App --render-check
              touch $out
            '';
          };

          # nix fmt + nix flake check (auto-wired by flakeModule)
          treefmt = {
            projectRootFile = "flake.nix";
            programs.fantomas.enable = true;
            programs.nixfmt.enable = true;

            settings.formatter.pg_format = {
              command = "${pkgs.pgformatter}/bin/pg_format";
              options = [
                "--inplace"
                "-f"
                "2"
              ];
              includes = [ "*.sql" ];
            };
          };

          # Native Nix devShells replacement
          devShells = {
            # nix develop .#ci
            ci = pkgs.mkShell {
              name = "ci-shell";
              buildInputs = [
                net10
                pkgs.gnumake
              ];

              # `dotnet run`/tests load the unpatched QuestPDF .so files from the NuGet cache.
              LD_LIBRARY_PATH = lib.makeLibraryPath questpdfLibs;

              shellHook = ''
                export TMPDIR="$(mktemp -d /tmp/nix-shell-XXXXXX)"
                echo "Entering CI shell..."
                dotnet --info
              '';
            };
          };

          devenv.shells.default = {
            devenv.root =
              let
                workingDirectory = builtins.getEnv "PWD";
              in
              if workingDirectory == "" then builtins.toString ./. else workingDirectory;

            # OCI packaging is defined above; disable devenv's implicit shell
            # containers so flake checks do not require unrelated container inputs.
            containers = lib.mkForce { };

            packages = with pkgs; [
              bash
              gnumake
              postgresql_19

              # for dotnet
              netcoredbg
              fsautocomplete
              fantomas
            ];

            languages.dotnet = {
              enable = true;
              package = net10;
            };

            # Development email capture: the app relays its encrypted outbox over SMTP to
            # Mailpit, which serves the mailbox UI and REST API on 8025. Chaos is enabled (all
            # probabilities default to 0) so SMTP failure behavior can be exercised through the
            # UI or API; duplicate suppression stays off because it would hide SMTP's real
            # at-least-once semantics.
            services.mailpit = {
              enable = true;
              smtpListenAddress = "127.0.0.1:1025";
              uiListenAddress = "127.0.0.1:8025";
              additionalArgs = [ "--enable-chaos" ];
            };

            # Mirrors the Automata repository's development database exactly:
            # same nixpkgs pin (flake.lock), same PostgreSQL 19 build, same
            # extension set and server settings. The pinned server is a
            # development dependency of ByzantineSystems.Automata 0.5.0 and
            # must not be updated independently of it (PLAN.md, P0 gate).
            services.postgres = {
              enable = true;
              package = pkgs.postgresql_19;
              extensions = ext: [
                ext.pg_cron
                ext.pgmq
              ];
              initdbArgs = [
                "--locale=C"
                "--encoding=UTF8"
              ];
              initialDatabases = [
                {
                  name = app_name;
                  user = app_name;
                  pass = app_name;
                }
              ];
              settings = {
                shared_preload_libraries = pkgs.lib.concatStringsSep "," [
                  "auto_explain"
                  "pg_cron"
                  "pg_stat_statements"
                ];
                session_preload_libraries = "auto_explain";
                "auto_explain.log_min_duration" = 150;
                "auto_explain.log_analyze" = true;
                log_min_duration_statement = 0;
                log_statement = "all";
                "cron.database_name" = "${app_name}";
                compute_query_id = "on";
                "pg_stat_statements.max" = 10000;
                "pg_stat_statements.track" = "all";
                shared_buffers = "1GB";
                work_mem = "16MB";
                huge_pages = "try";
                effective_io_concurrency = 16;
                maintenance_io_concurrency = 16;
              }
              // lib.optionalAttrs pkgs.stdenv.isLinux {
                io_method = "io_uring";
              }
              // lib.optionalAttrs pkgs.stdenv.isDarwin {
                io_method = "worker";
                io_workers = 8;
              };
              port = 5432;
              listen_addresses = "127.0.0.1";
              initialScript = ''
                ALTER USER ${app_name} CREATEDB CREATEROLE;
              '';
            };

            env.ConnectionStrings__App = "Host=127.0.0.1;Port=5432;Database=${app_name};Username=${app_name};Password=${app_name}";

            # Development email delivery through the Mailpit service above. Production
            # overrides every value explicitly; startup validation rejects loopback hosts and
            # missing TLS outside Development.
            env.Email__Host = "127.0.0.1";
            env.Email__Port = "1025";
            env.Email__UseTls = "false";
            env.Email__FromAddress = "fsnix@example.test";
            env.Email__FromName = "fsnix (dev)";
            env.Email__PublicOrigin = "http://localhost:5000";

            scripts = {
              migrate.exec = "dotnet run --project src/App/App.fsproj -- --migrate";
              db-connect.exec = "psql postgresql://${app_name}:${app_name}@127.0.0.1:5432/${app_name}";
              run-app.exec = "dotnet run --project src/App/App.fsproj";
            };

            # `dotnet run`/tests load the unpatched QuestPDF .so files from the NuGet cache.
            # Prepended here rather than via `env`, which devenv's dotnet module already sets
            # (ICU) and would conflict.
            enterShell = ''
              export LD_LIBRARY_PATH="${lib.makeLibraryPath questpdfLibs}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
              export TMPDIR="$(mktemp -d /tmp/nix-shell-XXXXXX)"
              echo "Starting Development Environment..."
            '';

            enterTest = ''
              make test
              make schema-check
            '';
          };
        };
    };
}
