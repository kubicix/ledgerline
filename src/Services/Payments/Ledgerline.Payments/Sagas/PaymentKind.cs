namespace Ledgerline.Payments.Sagas;

public enum PaymentKind { TopUp, Transfer, MerchantPayment }
public enum RefundStatus { Processing, Completed, Failed }
public enum CaptureMode { Manual, Automatic }
