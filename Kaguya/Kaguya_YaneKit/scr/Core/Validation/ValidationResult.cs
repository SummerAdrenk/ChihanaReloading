// ============================================================================
// ValidationResult.cs
// 验证结果集合
//
// 功能说明:
//   收集多条 ValidationIssue, 提供便捷的添加和查询方法
//
// 关键属性/方法:
//   Issues   - 只读问题列表 (ReadOnlyCollection 包装)
//   IsValid  - 当且仅当不存在 Error 级别问题时为 true
//   Add()    - 添加单条 ValidationIssue
//   AddInfo/AddWarning/AddError() - 按级别快捷添加
//   Merge()  - 合并另一个 ValidationResult 的所有问题
//
// 依赖: ValidationIssue, ValidationSeverity
// 被依赖: 各验证流程的调用方
// ============================================================================
using System.Collections.ObjectModel;

namespace Kaguya_YaneKit.Core.Validation;

public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = [];

    public IReadOnlyList<ValidationIssue> Issues => new ReadOnlyCollection<ValidationIssue>(_issues);

    public bool IsValid => !_issues.Any(issue => issue.Severity == ValidationSeverity.Error);

    public void Add(ValidationIssue issue) => _issues.Add(issue);

    public void AddInfo(string message, string? path = null, long? offset = null) =>
        Add(new ValidationIssue(ValidationSeverity.Info, message, path, offset));

    public void AddWarning(string message, string? path = null, long? offset = null) =>
        Add(new ValidationIssue(ValidationSeverity.Warning, message, path, offset));

    public void AddError(string message, string? path = null, long? offset = null) =>
        Add(new ValidationIssue(ValidationSeverity.Error, message, path, offset));

    public void Merge(ValidationResult other)
    {
        _issues.AddRange(other._issues);
    }
}
