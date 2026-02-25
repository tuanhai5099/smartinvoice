using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Application.Services;

/// <summary>Tạo UnitOfWork mới mỗi lần gọi (dùng cho worker nền để tránh dùng chung DbContext giữa nhiều thread).</summary>
public interface IUnitOfWorkFactory
{
    IUnitOfWork Create();
}
