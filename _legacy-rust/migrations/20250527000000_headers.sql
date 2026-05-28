-- Replace single auth_header column with a generic headers JSON column
ALTER TABLE servers ADD COLUMN headers TEXT NOT NULL DEFAULT '{}';

-- Migrate existing auth_header values into the new headers column
UPDATE servers SET headers = json_object('Authorization', auth_header) WHERE auth_header IS NOT NULL AND auth_header != '';

-- Drop the old column
ALTER TABLE servers DROP COLUMN auth_header;
