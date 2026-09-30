SELECT
    id
FROM
    fsnix.users
WHERE
    id = @user_id
FOR UPDATE
