-- Persisted user-facing module availability. Core modules remain enabled.
CREATE TABLE IF NOT EXISTS module_registry(
    module_key TEXT PRIMARY KEY,
    label TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    is_core INTEGER NOT NULL CHECK(is_core IN (0,1)),
    updated_ms INTEGER NOT NULL,
    updated_by TEXT NOT NULL
);
INSERT OR IGNORE INTO module_registry(module_key,label,enabled,is_core,updated_ms,updated_by) VALUES
 ('dashboard','运行总览',1,1,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('devices','设备管理',1,1,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('zte','ZTE 采集',1,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('h3c','H3C 事实回放',1,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('config-backups','配置备份',1,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('network-planning','网络规划',1,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('terminal','终端控制台',1,1,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('tasks','任务中心',1,1,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('wireless','无线业务',1,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('rail','轨道交通',0,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('tools','工具集',1,0,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('files','文件管理',1,1,CAST(strftime('%s','now') AS INTEGER)*1000,'system'),
 ('settings','系统设置',1,1,CAST(strftime('%s','now') AS INTEGER)*1000,'system');
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(17,CAST(strftime('%s','now') AS INTEGER)*1000);
