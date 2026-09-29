SELECT
    PG_NOTIFY('fsnix_feature_schedule_changed', @feature_name)
