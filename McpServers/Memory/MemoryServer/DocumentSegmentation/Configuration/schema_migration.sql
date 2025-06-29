-- Migration script for Document Segmentation feature
-- Version: Phase 1 - Core Foundation
-- Date: 2025-01-25

-- Create document_segments table
CREATE TABLE IF NOT EXISTS document_segments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_document_id INTEGER NOT NULL,  -- Changed from TEXT to INTEGER to match memories.id
    segment_id TEXT UNIQUE NOT NULL,
    sequence_number INTEGER NOT NULL,
    content TEXT NOT NULL,
    title TEXT,
    summary TEXT,
    coherence_score REAL DEFAULT 0.0,
    independence_score REAL DEFAULT 0.0,
    topic_consistency_score REAL DEFAULT 0.0,
    user_id TEXT NOT NULL,
    agent_id TEXT,
    run_id TEXT,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    metadata TEXT, -- JSON
    CONSTRAINT chk_document_segments_content_length CHECK (length(content) <= 50000),
    CONSTRAINT chk_document_segments_coherence_score CHECK (coherence_score >= 0.0 AND coherence_score <= 1.0),
    CONSTRAINT chk_document_segments_independence_score CHECK (independence_score >= 0.0 AND independence_score <= 1.0),
    CONSTRAINT chk_document_segments_topic_consistency_score CHECK (topic_consistency_score >= 0.0 AND topic_consistency_score <= 1.0),
    FOREIGN KEY (parent_document_id) REFERENCES memories(id) ON DELETE CASCADE
);

-- Create segment_relationships table
CREATE TABLE IF NOT EXISTS segment_relationships (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_segment_id TEXT NOT NULL,
    target_segment_id TEXT NOT NULL,
    relationship_type TEXT NOT NULL, -- 'sequential', 'hierarchical', 'referential', 'topical'
    strength REAL DEFAULT 1.0,
    user_id TEXT NOT NULL,
    agent_id TEXT,
    run_id TEXT,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    metadata TEXT, -- JSON
    CONSTRAINT chk_segment_relationships_strength CHECK (strength >= 0.0 AND strength <= 1.0),
    CONSTRAINT chk_segment_relationships_type CHECK (relationship_type IN ('sequential', 'hierarchical', 'referential', 'topical')),
    FOREIGN KEY (source_segment_id) REFERENCES document_segments(segment_id) ON DELETE CASCADE,
    FOREIGN KEY (target_segment_id) REFERENCES document_segments(segment_id) ON DELETE CASCADE
);

-- Create indexes for performance
CREATE INDEX IF NOT EXISTS idx_document_segments_parent ON document_segments(parent_document_id);
CREATE INDEX IF NOT EXISTS idx_document_segments_session ON document_segments(user_id, agent_id, run_id);
CREATE INDEX IF NOT EXISTS idx_document_segments_sequence ON document_segments(parent_document_id, sequence_number);
CREATE INDEX IF NOT EXISTS idx_document_segments_created ON document_segments(created_at DESC);

CREATE INDEX IF NOT EXISTS idx_segment_relationships_source ON segment_relationships(source_segment_id);
CREATE INDEX IF NOT EXISTS idx_segment_relationships_target ON segment_relationships(target_segment_id);
CREATE INDEX IF NOT EXISTS idx_segment_relationships_session ON segment_relationships(user_id, agent_id, run_id);
CREATE INDEX IF NOT EXISTS idx_segment_relationships_type ON segment_relationships(relationship_type);

-- FTS5 virtual table for segment content search
CREATE VIRTUAL TABLE IF NOT EXISTS document_segments_fts USING fts5(
    content,
    title,
    summary,
    metadata,
    content='document_segments',
    content_rowid='id'
);

-- FTS5 triggers for automatic indexing
CREATE TRIGGER IF NOT EXISTS document_segments_fts_insert AFTER INSERT ON document_segments BEGIN
    INSERT INTO document_segments_fts(rowid, content, title, summary, metadata) 
    VALUES (new.id, new.content, new.title, new.summary, new.metadata);
END;

CREATE TRIGGER IF NOT EXISTS document_segments_fts_update AFTER UPDATE ON document_segments BEGIN
    UPDATE document_segments_fts SET 
        content = new.content, 
        title = new.title, 
        summary = new.summary, 
        metadata = new.metadata 
    WHERE rowid = new.id;
END;

CREATE TRIGGER IF NOT EXISTS document_segments_fts_delete AFTER DELETE ON document_segments BEGIN
    DELETE FROM document_segments_fts WHERE rowid = old.id;
END;
