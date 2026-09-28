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

          checks.application = app;

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

              shellHook = ''
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

            enterShell = ''
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
