export interface GroupInfo {
  id: string;
  name: string;
  createdAt: string;
  screenIds: string[];
}

export interface GroupUpsert {
  name: string;
  screenIds: string[];
}

export interface GroupCommandResultItem {
  screenId: string;
  success: boolean;
  commandId?: string | null;
  error?: string | null;
}

export interface GroupCommandResponse {
  groupId: string;
  total: number;
  sent: number;
  offline: number;
  failed: number;
  results: GroupCommandResultItem[];
}
