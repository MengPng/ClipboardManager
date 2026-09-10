BEGIN;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TYPE workspace_kind AS ENUM ('personal','team');
CREATE TYPE workspace_role AS ENUM ('owner','admin','member');
CREATE TYPE object_state AS ENUM ('pending','ready','quarantined','deleted');

CREATE TABLE profiles (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), email text NOT NULL UNIQUE,
  password_hash text NOT NULL, display_name text NOT NULL, disabled_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE devices (
  id uuid PRIMARY KEY, profile_id uuid NOT NULL REFERENCES profiles(id), device_name text NOT NULL,
  refresh_token_hash bytea NOT NULL, last_seen_at timestamptz, revoked_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE auth_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), profile_id uuid NOT NULL REFERENCES profiles(id),
  device_id uuid NOT NULL REFERENCES devices(id), access_token_hash bytea NOT NULL UNIQUE,
  refresh_token_hash bytea NOT NULL UNIQUE, access_expires_at timestamptz NOT NULL,
  refresh_expires_at timestamptz NOT NULL, revoked_at timestamptz, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_auth_sessions_profile ON auth_sessions(profile_id,revoked_at);
CREATE TABLE workspaces (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), kind workspace_kind NOT NULL, name text NOT NULL,
  owner_id uuid NOT NULL REFERENCES profiles(id),
  created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), deleted_at timestamptz
);
CREATE UNIQUE INDEX ux_personal_workspace ON workspaces(owner_id) WHERE kind='personal' AND deleted_at IS NULL;
CREATE TABLE workspace_quotas (
  workspace_id uuid PRIMARY KEY REFERENCES workspaces(id), quota_bytes bigint NOT NULL CHECK(quota_bytes >= 0),
  used_bytes bigint NOT NULL DEFAULT 0 CHECK(used_bytes >= 0 AND used_bytes <= quota_bytes),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE workspace_members (
  workspace_id uuid NOT NULL REFERENCES workspaces(id), profile_id uuid NOT NULL REFERENCES profiles(id),
  role workspace_role NOT NULL, joined_at timestamptz NOT NULL DEFAULT now(), removed_at timestamptz,
  PRIMARY KEY(workspace_id,profile_id)
);
CREATE TABLE member_groups (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), workspace_id uuid NOT NULL REFERENCES workspaces(id),
  name text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), deleted_at timestamptz
);
CREATE UNIQUE INDEX ux_active_group_name ON member_groups(workspace_id,lower(name)) WHERE deleted_at IS NULL;
CREATE TABLE member_group_members (
  group_id uuid NOT NULL REFERENCES member_groups(id), profile_id uuid NOT NULL REFERENCES profiles(id),
  PRIMARY KEY(group_id,profile_id)
);
CREATE TABLE workspace_invitations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), workspace_id uuid NOT NULL REFERENCES workspaces(id),
  code_hash bytea NOT NULL UNIQUE, role workspace_role NOT NULL DEFAULT 'member', expires_at timestamptz NOT NULL,
  max_uses integer NOT NULL DEFAULT 1 CHECK(max_uses > 0), used_count integer NOT NULL DEFAULT 0,
  created_by uuid NOT NULL REFERENCES profiles(id), created_at timestamptz NOT NULL DEFAULT now(), revoked_at timestamptz,
  CHECK(used_count >= 0 AND used_count <= max_uses)
);
CREATE TABLE collection_folders (
  id uuid PRIMARY KEY, workspace_id uuid NOT NULL REFERENCES workspaces(id), parent_folder_id uuid REFERENCES collection_folders(id),
  name text NOT NULL, sort_key numeric NOT NULL, scope_all_members boolean NOT NULL DEFAULT true,
  version bigint NOT NULL DEFAULT 1, actor_id uuid NOT NULL REFERENCES profiles(id), updated_at timestamptz NOT NULL,
  deleted_at timestamptz
);
CREATE TABLE folder_group_grants (
  folder_id uuid NOT NULL REFERENCES collection_folders(id), group_id uuid NOT NULL REFERENCES member_groups(id),
  PRIMARY KEY(folder_id,group_id)
);
CREATE TABLE collection_items (
  id uuid PRIMARY KEY, workspace_id uuid NOT NULL REFERENCES workspaces(id), folder_id uuid NOT NULL REFERENCES collection_folders(id),
  item_type text NOT NULL CHECK(item_type IN ('text','link','image','file')), payload jsonb NOT NULL,
  blob_id uuid, sort_key numeric NOT NULL, version bigint NOT NULL DEFAULT 1,
  actor_id uuid NOT NULL REFERENCES profiles(id), updated_at timestamptz NOT NULL, deleted_at timestamptz
);
CREATE TABLE blob_objects (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), workspace_id uuid NOT NULL REFERENCES workspaces(id), sha256 bytea NOT NULL,
  byte_size bigint NOT NULL CHECK(byte_size BETWEEN 1 AND 104857600), original_name text NOT NULL,
  declared_mime text, detected_mime text, cos_key text NOT NULL UNIQUE, state object_state NOT NULL DEFAULT 'pending',
  created_by uuid NOT NULL REFERENCES profiles(id), created_at timestamptz NOT NULL DEFAULT now(), verified_at timestamptz,
  UNIQUE(workspace_id,sha256,byte_size)
);
ALTER TABLE collection_items ADD CONSTRAINT fk_collection_blob FOREIGN KEY(blob_id) REFERENCES blob_objects(id);
CREATE TABLE blob_references (
  blob_id uuid NOT NULL REFERENCES blob_objects(id), item_id uuid NOT NULL REFERENCES collection_items(id),
  created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(blob_id,item_id)
);
CREATE TABLE sync_operations (
  operation_id uuid PRIMARY KEY, workspace_id uuid NOT NULL REFERENCES workspaces(id), actor_id uuid NOT NULL REFERENCES profiles(id),
  device_id uuid NOT NULL REFERENCES devices(id), entity_type text NOT NULL, entity_id uuid NOT NULL,
  operation_type text NOT NULL, base_version bigint NOT NULL, result_version bigint,
  accepted_at timestamptz NOT NULL DEFAULT now(), result jsonb NOT NULL
);
CREATE TABLE sync_changes (
  sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, workspace_id uuid NOT NULL REFERENCES workspaces(id),
  entity_type text NOT NULL, entity_id uuid NOT NULL, version bigint NOT NULL, payload jsonb,
  deleted_at timestamptz, actor_id uuid NOT NULL REFERENCES profiles(id), changed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_sync_changes_cursor ON sync_changes(workspace_id,sequence);
CREATE TABLE sync_cursors (
  workspace_id uuid NOT NULL REFERENCES workspaces(id), device_id uuid NOT NULL REFERENCES devices(id),
  acknowledged_sequence bigint NOT NULL DEFAULT 0, updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(workspace_id,device_id)
);
CREATE TABLE conflict_copies (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(), workspace_id uuid NOT NULL REFERENCES workspaces(id),
  entity_type text NOT NULL, entity_id uuid NOT NULL, local_payload jsonb NOT NULL, remote_payload jsonb NOT NULL,
  local_version bigint NOT NULL, server_version bigint NOT NULL, device_id uuid REFERENCES devices(id), actor_id uuid REFERENCES profiles(id),
  created_at timestamptz NOT NULL DEFAULT now(), resolved_at timestamptz, resolution text
);
CREATE TABLE audit_events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, workspace_id uuid REFERENCES workspaces(id), actor_id uuid REFERENCES profiles(id),
  device_id uuid REFERENCES devices(id), event_type text NOT NULL, target_type text, target_id uuid,
  metadata jsonb NOT NULL DEFAULT '{}', created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE workspaces ENABLE ROW LEVEL SECURITY;
ALTER TABLE workspace_quotas ENABLE ROW LEVEL SECURITY;
ALTER TABLE workspace_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE member_groups ENABLE ROW LEVEL SECURITY;
ALTER TABLE member_group_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE workspace_invitations ENABLE ROW LEVEL SECURITY;
ALTER TABLE collection_folders ENABLE ROW LEVEL SECURITY;
ALTER TABLE folder_group_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE collection_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE blob_objects ENABLE ROW LEVEL SECURITY;
ALTER TABLE blob_references ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_operations ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_changes ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_cursors ENABLE ROW LEVEL SECURITY;
ALTER TABLE conflict_copies ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;

CREATE FUNCTION app_user_id() RETURNS uuid LANGUAGE sql STABLE AS
$$ SELECT nullif(current_setting('app.user_id', true),'')::uuid $$;
CREATE FUNCTION is_workspace_member(wid uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=public AS
$$ SELECT EXISTS(SELECT 1 FROM workspace_members m WHERE m.workspace_id=wid AND m.profile_id=app_user_id() AND m.removed_at IS NULL) $$;
CREATE FUNCTION can_edit_workspace(wid uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=public AS
$$ SELECT EXISTS(SELECT 1 FROM workspace_members m WHERE m.workspace_id=wid AND m.profile_id=app_user_id() AND m.removed_at IS NULL AND m.role IN ('owner','admin')) $$;
CREATE FUNCTION can_read_folder(fid uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=public AS
$$ SELECT EXISTS(
  SELECT 1 FROM collection_folders f
  WHERE f.id=fid AND is_workspace_member(f.workspace_id) AND (
    can_edit_workspace(f.workspace_id) OR f.scope_all_members OR EXISTS(
      SELECT 1 FROM folder_group_grants fg
      JOIN member_group_members mgm ON mgm.group_id=fg.group_id
      WHERE fg.folder_id=f.id AND mgm.profile_id=app_user_id()
    )
  )
) $$;
CREATE FUNCTION can_read_change(wid uuid, kind text, eid uuid, body jsonb) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=public AS
$$ SELECT can_edit_workspace(wid) OR CASE
  WHEN kind='collection_folder' THEN can_read_folder(eid)
  WHEN kind='collection_item' AND body ? 'folderId'
    AND body->>'folderId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
    THEN can_read_folder((body->>'folderId')::uuid)
  ELSE false
END $$;

CREATE POLICY workspace_read ON workspaces FOR SELECT USING (is_workspace_member(id));
CREATE POLICY quota_read ON workspace_quotas FOR SELECT USING (is_workspace_member(workspace_id));
CREATE POLICY member_read ON workspace_members FOR SELECT USING (is_workspace_member(workspace_id));
CREATE POLICY group_read ON member_groups FOR SELECT USING (is_workspace_member(workspace_id));
CREATE POLICY group_member_read ON member_group_members FOR SELECT USING (
  EXISTS(SELECT 1 FROM member_groups g WHERE g.id=group_id AND is_workspace_member(g.workspace_id))
);
CREATE POLICY invitation_manage ON workspace_invitations FOR ALL USING (can_edit_workspace(workspace_id)) WITH CHECK (can_edit_workspace(workspace_id));
CREATE POLICY folder_read ON collection_folders FOR SELECT USING (can_read_folder(id));
CREATE POLICY folder_write ON collection_folders FOR ALL USING (can_edit_workspace(workspace_id)) WITH CHECK (can_edit_workspace(workspace_id));
CREATE POLICY folder_grant_manage ON folder_group_grants FOR ALL USING (
  EXISTS(SELECT 1 FROM collection_folders f WHERE f.id=folder_id AND can_edit_workspace(f.workspace_id))
) WITH CHECK (EXISTS(SELECT 1 FROM collection_folders f WHERE f.id=folder_id AND can_edit_workspace(f.workspace_id)));
CREATE POLICY item_read ON collection_items FOR SELECT USING (can_read_folder(folder_id));
CREATE POLICY item_write ON collection_items FOR ALL USING (can_edit_workspace(workspace_id)) WITH CHECK (can_edit_workspace(workspace_id));
CREATE POLICY blob_read ON blob_objects FOR SELECT USING (
  can_edit_workspace(workspace_id) OR EXISTS(
    SELECT 1 FROM blob_references br JOIN collection_items i ON i.id=br.item_id
    WHERE br.blob_id=blob_objects.id AND can_read_folder(i.folder_id)
  )
);
CREATE POLICY blob_write ON blob_objects FOR ALL USING (can_edit_workspace(workspace_id)) WITH CHECK (can_edit_workspace(workspace_id));
CREATE POLICY blob_reference_read ON blob_references FOR SELECT USING (
  EXISTS(SELECT 1 FROM collection_items i WHERE i.id=item_id AND can_read_folder(i.folder_id))
);
CREATE POLICY operation_access ON sync_operations FOR ALL USING (is_workspace_member(workspace_id)) WITH CHECK (can_edit_workspace(workspace_id));
CREATE POLICY change_read ON sync_changes FOR SELECT USING (can_read_change(workspace_id,entity_type,entity_id,payload));
CREATE POLICY change_write ON sync_changes FOR INSERT WITH CHECK (can_edit_workspace(workspace_id));
CREATE POLICY cursor_access ON sync_cursors FOR ALL USING (is_workspace_member(workspace_id)) WITH CHECK (is_workspace_member(workspace_id));
CREATE POLICY conflict_access ON conflict_copies FOR ALL USING (is_workspace_member(workspace_id)) WITH CHECK (is_workspace_member(workspace_id));
CREATE POLICY audit_read ON audit_events FOR SELECT USING (workspace_id IS NOT NULL AND can_edit_workspace(workspace_id));
COMMIT;
