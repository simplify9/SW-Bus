namespace SW.Bus.SampleWeb.Models;

// ─── Order domain ────────────────────────────────────────────────────────────

public record OrderCreatedMessage(
    string OrderId,
    string CustomerId,
    decimal TotalAmount,
    int ItemCount,
    string Currency = "USD");

public record OrderCancelledMessage(
    string OrderId,
    string CustomerId,
    string Reason,
    bool UserInitiated);

public record OrderShippedMessage(
    string OrderId,
    string TrackingNumber,
    string Carrier,
    DateTime EstimatedDelivery);

// ─── Payment domain ─────────────────────────────────────────────────────────

public record PaymentProcessedMessage(
    string PaymentId,
    string OrderId,
    decimal Amount,
    string Currency,
    string Provider,     // Stripe, PayPal, etc.
    bool IsRefund = false);

public record PaymentFailedMessage(
    string PaymentId,
    string OrderId,
    decimal AttemptedAmount,
    string FailureCode,
    string FailureReason);

// ─── Inventory domain ────────────────────────────────────────────────────────

public record InventoryUpdatedMessage(
    string ProductId,
    string ProductName,
    int PreviousStock,
    int NewStock,
    string WarehouseId,
    string UpdatedBy);

public record LowStockAlertMessage(
    string ProductId,
    string ProductName,
    int CurrentStock,
    int MinimumThreshold,
    string WarehouseId);

// ─── Notification domain ─────────────────────────────────────────────────────

public record EmailNotificationMessage(
    string RecipientEmail,
    string Subject,
    string TemplateId,
    string Language = "en",
    int Priority = 0);   // 0 = normal, 1 = high

public record SmsNotificationMessage(
    string PhoneNumber,
    string Content,
    string SenderId);

public record PushNotificationMessage(
    string DeviceToken,
    string Title,
    string Body,
    string DeepLinkUrl);

// ─── Reporting domain ────────────────────────────────────────────────────────

public record DailyReportMessage(
    string ReportId,
    DateTime ReportDate,
    string ReportType,   // "Sales", "Inventory", "UserActivity"
    string RequestedBy);

// ─── Data sync domain ────────────────────────────────────────────────────────

public record DataSyncMessage(
    string SyncId,
    string EntityType,
    string EntityId,
    string Source,       // system emitting the change
    string Operation);   // "Create", "Update", "Delete"

// ─── Alert domain ────────────────────────────────────────────────────────────

public record UrgentAlertMessage(
    string AlertId,
    string AlertCode,
    string Severity,     // "P1", "P2", "P3"
    string Description,
    string AffectedSystem);

