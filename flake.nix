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
          dotnet = pkgs.dotnet-sdk_10;
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
            dotnet-sdk = dotnet;
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
                dotnet
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
              postgresql_18

              # for dotnet
              netcoredbg
              fsautocomplete
              fantomas
            ];

            languages.dotnet = {
              enable = true;
              package = dotnet;
            };

            services.postgres = {
              enable = true;
              package = pkgs.postgresql_18;
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
              port = 5432;
              listen_addresses = "127.0.0.1";
              initialScript = ''
                ALTER USER ${app_name} CREATEDB CREATEROLE;
              '';
            };

            env.ConnectionStrings__App = "Host=127.0.0.1;Port=5432;Database=${app_name};Username=${app_name};Password=${app_name}";

            scripts = {
              migrate.exec = "dotnet run --project src/App/App.fsproj -- --migrate";
              db-connect.exec = "psql postgresql://${app_name}:${app_name}@127.0.0.1:5432/${app_name}";
              run-app.exec = "dotnet run --project src/App/App.fsproj";
            };

            enterShell = ''
              echo "Starting Development Environment..."
            '';
          };
        };
    };
}
