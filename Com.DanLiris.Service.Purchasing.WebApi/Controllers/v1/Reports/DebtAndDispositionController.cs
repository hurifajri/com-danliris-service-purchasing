﻿using Com.DanLiris.Service.Purchasing.Lib.Facades.DebtAndDispositionSummary;
using Com.DanLiris.Service.Purchasing.Lib.PDFTemplates;
using Com.DanLiris.Service.Purchasing.Lib.Services;
using Com.DanLiris.Service.Purchasing.WebApi.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Com.DanLiris.Service.Purchasing.WebApi.Controllers.v1.Reports
{
    [Produces("application/json")]
    [ApiVersion("1.0")]
    [Route("v{version:apiVersion}/reports/debt-and-disposition-summaries")]
    [Authorize]
    public class DebtAndDispositionController : Controller
    {
        private readonly IDebtAndDispositionSummaryService _service;
        private readonly IdentityService _identityService;
        private const string ApiVersion = "1.0";

        public DebtAndDispositionController(IServiceProvider serviceProvider)
        {
            _service = serviceProvider.GetService<IDebtAndDispositionSummaryService>();
            _identityService = serviceProvider.GetService<IdentityService>();
        }

        private void VerifyUser()
        {
            _identityService.Username = User.Claims.ToArray().SingleOrDefault(p => p.Type.Equals("username")).Value;
            _identityService.Token = Request.Headers["Authorization"].FirstOrDefault().Replace("Bearer ", "");
            _identityService.TimezoneOffset = Convert.ToInt32(Request.Headers["x-timezone-offset"]);
        }

        [HttpGet]
        public IActionResult Get([FromQuery] int categoryId, [FromQuery] int unitId, [FromQuery] int divisionId, [FromQuery] DateTimeOffset? dueDate, [FromQuery] bool isImport, [FromQuery] bool isForeignCurrency)
        {

            try
            {
                if (!dueDate.HasValue)
                    dueDate = DateTimeOffset.Now;
                var result = _service.GetReport(categoryId, unitId, divisionId, dueDate.GetValueOrDefault(), isImport, isForeignCurrency);
                return Ok(new
                {
                    apiVersion = ApiVersion,
                    statusCode = General.OK_STATUS_CODE,
                    message = General.OK_MESSAGE,
                    data = result,
                    info = new Dictionary<string, object>
                    {
                        { "page", 1 },
                        { "size", 10 }
                    },
                });
            }
            catch (Exception e)
            {
                return StatusCode(General.INTERNAL_ERROR_STATUS_CODE, e.Message + " " + e.StackTrace);
            }
        }

        [HttpGet("download-excel")]
        public IActionResult DownloadExcel([FromQuery] int categoryId, [FromQuery] int unitId, [FromQuery] int divisionId, [FromQuery] DateTimeOffset? dueDate, [FromQuery] bool isImport, [FromQuery] bool isForeignCurrency)
        {

            try
            {
                if (!dueDate.HasValue)
                    dueDate = DateTimeOffset.Now;

                var result = _service.GetSummary(categoryId, unitId, divisionId, dueDate.GetValueOrDefault(), isImport, isForeignCurrency);

                var stream = GenerateExcel(result, _identityService.TimezoneOffset, dueDate.GetValueOrDefault(), unitId, isImport, isForeignCurrency);

                var filename = "LAPORAN REKAP DATA HUTANG DAN DISPOSISI";
                filename += ".xlsx";

                var bytes = stream.ToArray();

                return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
            }
            catch (Exception e)
            {
                var result = new ResultFormatter(ApiVersion, General.INTERNAL_ERROR_STATUS_CODE, e.Message).Fail();
                return StatusCode(General.INTERNAL_ERROR_STATUS_CODE, result);
            }
        }

        private MemoryStream GenerateExcel(List<DebtAndDispositionSummaryDto> data, int timezoneOffset, DateTimeOffset dueDate, int unitId, bool isImport, bool isForeignCurrency)
        {
            var company = "PT DAN LIRIS";
            var title = "LAPORAN REKAP DATA HUTANG & DISPOSISI LOKAL";
            var unitName = "SEMUA UNIT";
            var date = $"JATUH TEMPO S.D. {dueDate:yyyy-dd-MM}";

            if (unitId > 0)
            {
                var datum = data.FirstOrDefault();
                if (datum != null)
                    unitName = datum.UnitName;

            }

            if (isForeignCurrency && !isImport)
                title = "LAPORAN REKAP DATA HUTANG & DISPOSISI LOKAL VALAS";

            if (isImport)
                title = "LAPORAN REKAP DATA HUTANG & DISPOSISI IMPORT";

            var categoryDataTable = GetCategoryDataTable(data);

            if (!isImport && !isForeignCurrency)
            {
                var unitDataTable = GetUnitDataTable(data);

                using (var package = new ExcelPackage())
                {
                    var worksheet = package.Workbook.Worksheets.Add("Sheet 1");
                    worksheet.Cells["A1"].Value = company;
                    worksheet.Cells["A2"].Value = title;
                    worksheet.Cells["A3"].Value = unitName;
                    worksheet.Cells["A4"].Value = date;
                    worksheet.Cells["A6"].LoadFromDataTable(categoryDataTable, true);
                    worksheet.Cells[$"A{6 + 3 + categoryDataTable.Rows.Count}"].LoadFromDataTable(unitDataTable, true);

                    var stream = new MemoryStream();
                    package.SaveAs(stream);

                    return stream;
                }
            }
            else
            {
                var unitCurrencyDataTable = GetUnitCurrencyDataTable(data);
                var separatedUnitCurrencyDataTable = GetSeparatedUnitCurrencyDataTable(data);

                using (var package = new ExcelPackage())
                {
                    var worksheet = package.Workbook.Worksheets.Add("Sheet 1");
                    worksheet.Cells["A1"].Value = company;
                    worksheet.Cells["A2"].Value = title;
                    worksheet.Cells["A3"].Value = unitName;
                    worksheet.Cells["A4"].Value = date;
                    worksheet.Cells["A6"].LoadFromDataTable(categoryDataTable, true);
                    worksheet.Cells[$"A{6 + 3 + categoryDataTable.Rows.Count}"].LoadFromDataTable(unitCurrencyDataTable, true);
                    worksheet.Cells[$"A{6 + 3 + categoryDataTable.Rows.Count + 3 + unitCurrencyDataTable.Rows.Count}"].LoadFromDataTable(separatedUnitCurrencyDataTable, true);

                    var stream = new MemoryStream();
                    package.SaveAs(stream);

                    return stream;
                }
            }
        }

        private DataTable GetSeparatedUnitCurrencyDataTable(List<DebtAndDispositionSummaryDto> data)
        {
            var units = data.Select(element => element.UnitName).Distinct().ToList();

            var debtData = data.Where(element => element.DispositionTotal == 0);
            var dispositionData = data.Where(element => element.DebtTotal == 0);

            var table = new DataTable();

            table.Columns.Add(new DataColumn() { ColumnName = "Unit", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = " ", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Mata Uang", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Total (IDR)", DataType = typeof(string) });

            foreach (var unit in units)
            {
                var currencyDebtData = debtData
                    .Where(element => element.UnitName == unit)
                    .GroupBy(element => element.CurrencyCode)
                    .Select(element => new DebtAndDispositionSummaryDto()
                    {
                        CurrencyCode = element.Key,
                        DebtTotal = element.Sum(sum => sum.DebtTotal * sum.CurrencyRate),
                    })
                    .ToList();

                var currencyDispositionData = dispositionData
                    .Where(element => element.UnitName == unit)
                    .GroupBy(element => element.CurrencyCode)
                    .Select(element => new DebtAndDispositionSummaryDto()
                    {
                        CurrencyCode = element.Key,
                        DispositionTotal = element.Sum(sum => sum.DispositionTotal * sum.CurrencyRate),
                    })
                    .ToList();

                table.Rows.Add("", "Hutang", "", "");
                foreach (var currencyDebt in currencyDebtData)
                {
                    table.Rows.Add("", "", currencyDebt.CurrencyCode, currencyDebt.DebtTotal.ToString());
                }

                table.Rows.Add("", "Disposisi", "", "");
                foreach (var currencyDisposition in currencyDispositionData)
                {
                    table.Rows.Add("", "", currencyDisposition.CurrencyCode, currencyDisposition.DispositionTotal.ToString());
                }
            }

            return table;
        }

        private DataTable GetUnitCurrencyDataTable(List<DebtAndDispositionSummaryDto> data)
        {
            var units = data.Select(element => element.UnitName).Distinct().ToList();

            var table = new DataTable();

            table.Columns.Add(new DataColumn() { ColumnName = "Unit", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Mata Uang", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Total (IDR)", DataType = typeof(string) });

            if (units.Count > 0)
            {
                foreach (var unit in units)
                {
                    var currencyData = data
                        .Where(element => element.UnitName == unit)
                        .GroupBy(element => element.CurrencyCode)
                        .Select(element => new DebtAndDispositionSummaryDto()
                        {
                            CurrencyCode = element.Key,
                            DebtTotal = element.Sum(sum => sum.DebtTotal),
                            DispositionTotal = element.Sum(sum => sum.DispositionTotal),
                            Total = element.Sum(sum => sum.DebtTotal * sum.CurrencyRate) + element.Sum(sum => sum.DispositionTotal * sum.CurrencyRate)
                        })
                        .ToList();

                    table.Rows.Add(unit, "", "");

                    foreach (var currency in currencyData)
                    {
                        table.Rows.Add("", currency.CurrencyCode, currency.Total.ToString());
                    }
                }
            }

            return table;
        }

        private DataTable GetUnitDataTable(List<DebtAndDispositionSummaryDto> data)
        {
            var units = data.Select(element => element.UnitName).Distinct().ToList();


            var table = new DataTable();

            table.Columns.Add(new DataColumn() { ColumnName = "Unit", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = " ", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Total (IDR)", DataType = typeof(string) });

            if (units.Count > 0)
            {
                foreach (var unit in units)
                {
                    var debtTotal = data.Where(element => element.UnitName == unit).Sum(sum => sum.DebtTotal);
                    var dispositionTotal = data.Where(element => element.UnitName == unit).Sum(sum => sum.DispositionTotal);

                    table.Rows.Add(unit, "", "");
                    table.Rows.Add("", "HUTANG", debtTotal.ToString());
                    table.Rows.Add("", "DISPOSISI", dispositionTotal.ToString());
                }
            }

            return table;
        }

        private DataTable GetCategoryDataTable(List<DebtAndDispositionSummaryDto> data)
        {
            var categoryData = data
               .GroupBy(element => new { element.CategoryCode, element.CurrencyCode })
               .Select(element => new DebtAndDispositionSummaryDto()
               {
                   CategoryCode = element.Key.CategoryCode,
                   CategoryName = element.FirstOrDefault().CategoryName,
                   CurrencyCode = element.Key.CurrencyCode,
                   DebtTotal = element.Sum(sum => sum.DebtTotal),
                   DispositionTotal = element.Sum(sum => sum.DispositionTotal),
                   Total = element.Sum(sum => sum.DebtTotal) + element.Sum(sum => sum.DispositionTotal)
               })
               .ToList();

            var table = new DataTable();

            table.Columns.Add(new DataColumn() { ColumnName = "Kategori", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Mata Uang", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Hutang", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Disposisi", DataType = typeof(string) });
            table.Columns.Add(new DataColumn() { ColumnName = "Total", DataType = typeof(string) });

            if (categoryData.Count > 0)
            {
                foreach (var categoryDatum in categoryData)
                {
                    table.Rows.Add(categoryDatum.CategoryName, categoryDatum.CurrencyCode, categoryDatum.DebtTotal, categoryDatum.DispositionTotal, categoryDatum.Total);
                }
            }

            return table;
        }

        [HttpGet("download-pdf")]
        public IActionResult DownloadPdf([FromQuery] int categoryId, [FromQuery] int unitId, [FromQuery] int divisionId, [FromQuery] DateTimeOffset? dueDate, [FromQuery] bool isImport, [FromQuery] bool isForeignCurrency)
        {

            try
            {
                if (!dueDate.HasValue)
                    dueDate = DateTimeOffset.Now;

                var result = _service.GetSummary(categoryId, unitId, divisionId, dueDate.GetValueOrDefault(), isImport, isForeignCurrency);

                var stream = DebtAndDispositionSummaryPDFTemplate.Generate(result, _identityService.TimezoneOffset, dueDate.GetValueOrDefault(), unitId, isImport, isForeignCurrency);

                var filename = "LAPORAN REKAP DATA HUTANG DAN DISPOSISI";
                filename += ".pdf";

                return new FileStreamResult(stream, "application/pdf")
                {
                    FileDownloadName = filename
                };
            }
            catch (Exception e)
            {
                var result = new ResultFormatter(ApiVersion, General.INTERNAL_ERROR_STATUS_CODE, e.Message).Fail();
                return StatusCode(General.INTERNAL_ERROR_STATUS_CODE, result);
            }
        }
    }
}
