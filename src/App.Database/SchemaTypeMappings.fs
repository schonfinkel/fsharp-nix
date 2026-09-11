namespace App.Database

open System.Data
open SqlHydra.Domain

type PostgreSqlTypeMappings() =
    interface IExtendTypeMapping with
        member _.Extend(baseTryFind) =
            fun context ->
                match context.Column.ProviderTypeName.ToLowerInvariant() with
                | "tstzrange" ->
                    Some
                        { ColumnTypeAlias = "tstzrange"
                          ClrType = "NpgsqlTypes.NpgsqlRange<System.DateTime>"
                          DbType = DbType.Object
                          ProviderDbType = Some "TimestampTzRange" }
                | _ -> baseTryFind context
