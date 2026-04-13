/**
 * Represents a chat mode that defines a persona, system prompt, and available tools.
 */
export interface ChatMode {
  id: string;
  name: string;
  description?: string;
  systemPrompt: string;
  enabledTools?: string[];
  isSystemDefined: boolean;
  createdAt: number;
  updatedAt: number;
}

/**
 * Request body for creating or updating a chat mode.
 */
export interface ChatModeCreateUpdate {
  name: string;
  description?: string;
  systemPrompt: string;
  enabledTools?: string[];
}

/**
 * Request body for copying a chat mode.
 */
export interface ChatModeCopy {
  newName: string;
}

/**
 * Represents a tool definition.
 */
export interface ToolDefinition {
  name: string;
  description?: string;
}

/**
 * Request body for switching conversation mode.
 */
export interface SwitchModeRequest {
  modeId: string;
}

/**
 * Response from switching conversation mode.
 */
export interface SwitchModeResponse {
  modeId: string;
  modeName: string;
}
