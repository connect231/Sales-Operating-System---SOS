using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SOS.Models.MsK;

[Table("TBL_VARUNA_SIPARIS")]
public partial class TBL_VARUNA_SIPARI
{
    [Key]
    public int LNGKOD { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? CreateOrderDate { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? OrderId { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? InvoiceDate { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? PaymentType { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? PaymentTypeTime { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? OrderStatus { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? QuoteId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? AccountId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? ProposalOwnerId { get; set; }

    [Column(TypeName = "money")]
    public decimal? SubTotalDiscount { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? CompanyId { get; set; }

    public bool? IsEligibleForNetsisIntegration { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? SAPOutReferenceCode { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? DistributionChannelSapId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? DivisionSapId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? SalesDocumentTypeSapId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? SalesOrganizationSapId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? CrmSalesOfficeSapId { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? SalesGroupSapId { get; set; }

    public bool? IsEligibleForSapIntegration { get; set; }

    [StringLength(512)]
    [Unicode(false)]
    public string? CrmOrderNotes { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? SerialNumber { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? CreatedOn { get; set; }

    [StringLength(128)]
    [Unicode(false)]
    public string? CreatedBy { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ModifiedOn { get; set; }

    [StringLength(128)]
    [Unicode(false)]
    public string? ModifiedBy { get; set; }

    [Column(TypeName = "money")]
    public decimal? TotalNetAmount { get; set; }

    [Column(TypeName = "money")]
    public decimal? TotalAmountWithTax { get; set; }

    [Column(TypeName = "money")]
    public decimal? TotalProfitAmount { get; set; }

    [StringLength(512)]
    [Unicode(false)]
    public string? AccountTitle { get; set; }

    [StringLength(64)]
    [Unicode(false)]
    public string? AccountSAPOutReferenceCode { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? DeletedOn { get; set; }

    [StringLength(128)]
    [Unicode(false)]
    public string? DeletedBy { get; set; }
}

