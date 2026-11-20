export type UserRole = "Admin" | "Viewer";

export interface AuthUser {
  id: string;
  username: string;
  displayName: string;
  role: UserRole | string;
}

export interface LoginResponse {
  token: string;
  expiresAtUtc: string;
  user: AuthUser;
}
