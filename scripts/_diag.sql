-- Most recent request and its status
SELECT "Id", "Status", "SubmittedAt", "RequesterUpn"
FROM elevation_requests
ORDER BY "SubmittedAt" DESC LIMIT 5;

-- Comments in DB
SELECT c."Id", c."RequestId", c."IsAdmin", c."Body", c."CreatedAt"
FROM request_comments c
ORDER BY c."CreatedAt" DESC LIMIT 5;
