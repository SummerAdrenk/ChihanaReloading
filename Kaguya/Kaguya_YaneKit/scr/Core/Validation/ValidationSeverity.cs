// ============================================================================
// ValidationSeverity.cs
// 验证严重级别枚举
//
// 枚举值:
//   Info    - 信息提示 (不影响验证通过)
//   Warning - 警告 (不影响验证通过, 但需关注)
//   Error   - 错误 (导致验证失败)
//
// 依赖: 无外部依赖
// 被依赖: ValidationIssue, ValidationResult
// ============================================================================
namespace Kaguya_YaneKit.Core.Validation;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}
