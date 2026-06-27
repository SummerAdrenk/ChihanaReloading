// ============================================================================
// ValidationIssue.cs
// 单条验证问题记录
//
// 数据结构 (不可变 record):
//   Severity - 严重级别 (Info/Warning/Error)
//   Message  - 问题描述文本
//   Path     - 相关文件路径 (可选)
//   Offset   - 文件中的偏移位置 (可选)
//
// 依赖: ValidationSeverity
// 被依赖: ValidationResult
// ============================================================================
namespace Kaguya_YaneKit.Core.Validation;

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Message,
    string? Path = null,
    long? Offset = null);
