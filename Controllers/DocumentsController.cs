using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AutoMapper;
using DocumentProcessing.Abstraction;
using DocumentProcessing.Data;
using DocumentProcessing.Helpers;
using DocumentProcessing.Models;
using DocumentProcessing.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DocumentProcessing.Controllers
{
    [Authorize]
    public class DocumentsController : Controller
    {
        public string VisaID = "";
        private const int PageSize = 10;

        private readonly IFileUploader _fileUploader;
        private readonly IElectronicStamp _electronicStamp;
        private readonly ILogger<DocumentsController> _logger;
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public DocumentsController(
            IFileUploader fileUploader,
            IElectronicStamp electronicStamp,
            ILogger<DocumentsController> logger,
            ApplicationDbContext context,
            IMapper mapper)
        {
            _fileUploader = fileUploader;
            _electronicStamp = electronicStamp;
            _logger = logger;
            _context = context;
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            [FromQuery(Name = "q")] string searchText,
            [FromQuery(Name = "dateFrom")] string startDate,
            [FromQuery(Name = "dateFor")] string endDate,
            int? pageNumber)
        {
            var documents = _context.Documents.OrderByDescending(x => x.Date)
                .Include(x => x.Applicant)
                .Include(x => x.Purpose)
                .Include(x => x.Status)
                .Include(x => x.VisaType)
                .Include(x => x.VisaDateType)
                .Include(x => x.ScannedFiles)
                .Include(x => x.Owner)
                .Include(x => x.Recipient)
                .Include(x => x.Appointment).AsQueryable();
            
            if (!string.IsNullOrEmpty(endDate) && !string.IsNullOrEmpty(startDate))
            {
                var parsedStartDate = DateTime.ParseExact(startDate, "dd.MM.yyyy", CultureInfo.InvariantCulture);
                var parsedEndDate = DateTime.ParseExact(endDate, "dd.MM.yyyy", CultureInfo.CurrentCulture);

                ViewBag.DateFor = endDate;
                ViewBag.DateFrom = startDate;
                documents = documents.Where(x => x.Date >= parsedStartDate && x.Date < parsedEndDate);
            }

            if (!string.IsNullOrEmpty(searchText))
            {
                ViewBag.SearchText = searchText;
                documents = documents.Where(Search(searchText));
            }

            _logger.LogInformation(documents.ToSql());

            var list = await MappedPaginatedList<DocumentListViewModel>
                .CreateAsync(documents, _mapper, pageNumber ?? 1, PageSize);

            return View(list);
        }

        private Expression<Func<Document, bool>> Search(string searchText)
        {
            var patternMatch = $"%{searchText}%";
            return x => EF.Functions.ILike(x.Applicant.Name, patternMatch)
                        || EF.Functions.ILike(x.Owner.Name, patternMatch)
                        || EF.Functions.ILike(x.EntryNumber.ToString(), patternMatch)
                        || EF.Functions.ILike(x.Recipient.Name, patternMatch)
                        || EF.Functions.ILike(x.Purpose.Name, patternMatch)
                        || EF.Functions.ILike(x.Status.Name, patternMatch)
                        || EF.Functions.ILike(x.VisaType.Name, patternMatch)
                        || EF.Functions.ILike(x.VisaDateType.Name, patternMatch);
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            var selectedOwner = await _context.DocumentOwners
                .OrderBy(x => x.Name)
                .FirstOrDefaultAsync();

            PopulateOwnersDropDownList(selectedOwner);
            PopulateApplicantsDropDownList();
            PopulateStatusesDropDownList();
            PopulateVisaTypeDropDownList();
            PopulateVisaDateTypeDropDownList();
            PopulatePurposesDropDownList();

            return View(new DocumentViewModel());
        }

        [HttpPost]
        public async Task<IActionResult> Create(DocumentViewModel viewModel, IList<IFormFile> files, IFormCollection ifc)
        {
            string test = ifc["VisaId"];
            VisaID = test;
            if (ModelState.IsValid)
            {
                await CreateApplicantIfNotExist(viewModel);
                await CreatePurposeIfNotExist(viewModel);
                var document = _mapper.Map<Document>(viewModel);
                try
                {
                    document.VisaId = VisaID;
                    string[] strArr = null;
                    RequestId req = new RequestId();
                    char[] splitchar = { ',' };
                    if (String.IsNullOrEmpty(VisaID)) {  } else { strArr = VisaID.Split(splitchar); }

                
                  
                    await _context.AddAsync(document);
                    await _context.SaveChangesAsync();
                    
                    if (files.Any())
                    {
                        var newFiles = await _fileUploader.GetScannedFilesForDocument(document, files);
                        await _context.ScannedFiles.AddRangeAsync(newFiles);
                        await _context.SaveChangesAsync();
                    }
                    if (String.IsNullOrEmpty(VisaID)) { }
                    else
                    {
                        foreach (var id in strArr)
                        {

                            req.Number = id;
                            req.DocumentId = document.Id;
                            req.Id = Guid.NewGuid();

                            await _context.AddAsync(req);
                            await _context.SaveChangesAsync();
                        }
                    }

                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex) when (ex.GetType() == typeof(DbUpdateException))
                {
                    ModelState.AddModelError("", "Unable to save changes. " +
                                                 "Try again, and if the problem persists, " +
                                                 "see your system administrator.");
                }
            }

            SetSelectedDropDownLists(viewModel);
            return View(viewModel);
        }

        [HttpPost]
        public ActionResult Progress()
        {
            var progressResult = HttpContext.Session.GetInt32("progress").ToString();
            return Content(progressResult);
        }

        public IActionResult Edit(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var document = _context.Documents
                .Include(x => x.ScannedFiles)
                .Include(x => x.Appointment)
                .FirstOrDefault(x => x.Id == id);

            var request_Id = _context.RequestId.AsQueryable();
            request_Id = request_Id.Where(x => x.DocumentId == document.Id);
            var request_Id_Count = request_Id.Count();
            ViewBag.reg = request_Id;
            ViewBag.reqCount = request_Id_Count;
            
            if (document == null)
            {
                return NotFound();
            }

            var viewModel = _mapper.Map<DocumentViewModel>(document);
            viewModel.ApplicantType = ApplicantType.Existing;
            viewModel.PurposeType = PurposeType.Existing;

            SetSelectedDropDownLists(document);

            return View(viewModel);
        }

        [HttpPost, ActionName("Edit")]
        public async Task<IActionResult> EditPost(DocumentViewModel viewModel, IList<IFormFile> files, IFormCollection ifc)
        {
            string test = ifc["VisaId"];
            string[] strArr = null;
            RequestId req = new RequestId();
            char[] splitchar = { ',' };

            VisaID = test;
            if (String.IsNullOrEmpty(VisaID)) { } else { strArr = VisaID.Split(splitchar); }

            if (ModelState.IsValid)
            {
                var originalDocument = _context.Documents
                    .Include(x => x.Appointment)
                    .AsNoTracking()
                    .FirstOrDefault(x => x.Id == viewModel.Id);

                if (originalDocument == null)
                {
                    return NotFound();
                }

                await CreateApplicantIfNotExist(viewModel);
                await CreatePurposeIfNotExist(viewModel);
                var document = _mapper.Map<Document>(viewModel);
                document.Date = originalDocument.Date;

                var request_Id = _context.RequestId.AsQueryable();
                request_Id = request_Id.Where(x => x.DocumentId == document.Id);
                var request_Id_Count = request_Id.Count();
                ViewBag.reg = request_Id;
                ViewBag.reqCount = request_Id_Count;
                var hasAddedFiles = files.Any();
                var hasRequestIdChanges = false;
                var hasDocumentChanges = HasChangesBetweenTwoDocuments(originalDocument, document);
                var hasRemovedFiles = viewModel.ScannedFiles.Any(x => x.IsDeleted);
                bool hasError = false;

              

                try
                {
                    _context.RemoveRange(request_Id);
                    await _context.SaveChangesAsync();
                    hasRequestIdChanges = true;
                }
                catch (DbUpdateException ex)
                {
                    ModelState.AddModelError("", "Unable to save changes. " +
                                                 "Try again, and if the problem persists, " +
                                                 "see your system administrator." + ex.ToString());

                    hasError = true;
                }

                if (String.IsNullOrEmpty(test)) { }
                else
                {
                    foreach (var id in strArr)
                    {

                        req.Number = id;
                        req.DocumentId = document.Id;
                        req.Id = Guid.NewGuid();

                        await _context.AddAsync(req);
                        await _context.SaveChangesAsync();
                        hasRequestIdChanges = true;
                    }
                }

                if (hasAddedFiles)
                {
                    try
                    {
                        var scannedFiles = await _fileUploader.GetScannedFilesForDocument(document, files);
                        foreach (var scannedFile in scannedFiles)
                        {
                            scannedFile.DocumentId = originalDocument.Id;
                            await _context.ScannedFiles.AddAsync(scannedFile);
                        }

                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex)
                    {
                        ModelState.AddModelError("", "Unable to save changes. " +
                                                     "Try again, and if the problem persists, " +
                                                     "see your system administrator." + ex.ToString());

                        hasError = true;
                    }
                }

                if (hasRemovedFiles)
                {
                    var deletedScannedViewModels = viewModel
                        .ScannedFiles
                        .Where(x => x.IsDeleted)
                        .Select(x => x.Id);

                    var deletedFiles =
                        _context.ScannedFiles.Select(x => x.Id)
                            .Where(id => deletedScannedViewModels.Contains(id))
                            .Select(id => new ScannedFile {Id = id});

                    try
                    {
                        _context.RemoveRange(deletedFiles);
                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex)
                    {
                        ModelState.AddModelError("", "Unable to save changes. " +
                                                     "Try again, and if the problem persists, " +
                                                     "see your system administrator." + ex.ToString());

                        hasError = true;
                    }
                }

                if (hasDocumentChanges)
                {
                    try
                    {
                        if (originalDocument.Appointment == null)
                        {
                            var createdAppointment = document.Appointment;
                            createdAppointment.DocumentId = originalDocument.Id;

                            _context.Add(document.Appointment);
                            _context.SaveChanges();
                        }
                        

                        _context.Update(document);
                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex)
                    {
                        ModelState.AddModelError("", "Unable to save changes. " +
                                                     "Try again, and if the problem persists, " +
                                                     "see your system administrator." + ex.ToString());

                        hasError = true;
                    }
                }

                if ((hasAddedFiles || hasDocumentChanges || hasRemovedFiles || hasRequestIdChanges) && !hasError)
                {
                    return RedirectToAction(nameof(Index));
                }

                ModelState.AddModelError("", "Has not changes!");
            }

            SetSelectedDropDownLists(viewModel);
            return View(viewModel);
        }
        
        [HttpGet]
        public IActionResult PreView(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var document = _context.Documents
                .Where(x => x.Id == id)
                .Include(x => x.Owner)
                .Include(x => x.Applicant)
                .Include(x => x.Recipient)
                .Include(x => x.Purpose)
                .Include(x => x.Status)
                .Include(x => x.VisaType)
                .Include(x => x.VisaDateType)
                .Include(x => x.Appointment)

                .FirstOrDefault();

            var request_Id = _context.RequestId.AsQueryable();
            request_Id = request_Id.Where(x => x.DocumentId == document.Id);
            var request_Id_Count = request_Id.Count();
            string ReqId = "";
            var count = 0;
            foreach (var i in request_Id)
            {
                count++;
                if (count == 1)
                {
                    ReqId = i.Number;
                }
                else
                {
                    ReqId = ReqId + ',' + i.Number;
                }
            }
            ViewBag.ReqId = ReqId;
            var result = _mapper.Map<DocumentListViewModel>(document);

            return View(result);
        }
        [HttpGet]
        public IActionResult Delete(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var document = _context.Documents
                .Where(x => x.Id == id)
                .Include(x => x.Owner)
                .Include(x => x.Applicant)
                .Include(x => x.Recipient)
                .Include(x => x.Purpose)
                .Include(x => x.Status)
                .Include(x => x.VisaType)
                .Include(x => x.Appointment)
                .Include(x => x.VisaDateType)

                .FirstOrDefault();

            var request_Id = _context.RequestId.AsQueryable();
            request_Id = request_Id.Where(x => x.DocumentId == document.Id);
            var request_Id_Count = request_Id.Count();
            string ReqId = "";
            var count = 0;
            foreach (var i in request_Id)
            {
                count++;
                if (count == 1)
                {
                    ReqId = i.Number;
                }
                else
                {
                    ReqId = ReqId + ',' + i.Number;
                }
            }
            ViewBag.ReqId = ReqId;
            var result = _mapper.Map<DocumentListViewModel>(document);

            return View(result);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(Guid id)
        {
            var course = await _context.Documents.FindAsync(id);
            _context.Documents.Remove(course);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult File(Guid id)
        {
            var file = _context.ScannedFiles.FirstOrDefault(x => x.Id == id);
            if (file == null)
            {
                return NotFound();
            }

            return File(file.File, file.ContentType, file.FileName);
        }

        private bool HasChangesBetweenTwoDocuments(Document originalDocument, Document document)
        {
            return originalDocument.ApplicantId != document.ApplicantId
                   || originalDocument.Appointment?.Character != document.Appointment.Character
                   || originalDocument.Appointment?.Number != document.Appointment.Number
                   || originalDocument.Appointment?.Number != document.Appointment.Number
                   || originalDocument.VisaId != document.VisaId
                   || originalDocument.Description != document.Description
                   || originalDocument.VisaDate != document.VisaDate
                   || originalDocument.OwnerId != document.OwnerId
                   || originalDocument.PurposeId != document.PurposeId
                   || originalDocument.StatusId != document.StatusId
                   || originalDocument.VisaTypeId != document.VisaTypeId
                   || originalDocument.VisaDateTypeId != document.VisaDateTypeId
                   || originalDocument.ApplicantId != document.ApplicantId;
        }

        [HttpGet]
        public IActionResult Print(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }
           

            var document = _context.Documents
                .Where(x => x.Id == id)
                .Include(x => x.Owner)
                .Include(x => x.Applicant)
                .Include(x => x.Purpose)
                .Include(x => x.VisaType)
                //.Include(x => x.VisaId)
                .Include(x => x.VisaDateType)
                //.Include(x => x.VisaDate)
                .Include(x => x.Recipient)
                .FirstOrDefault();

            if (document == null)
            {
                return NotFound();
            }
            var request_Id = _context.RequestId.AsQueryable();
            request_Id = request_Id.Where(x => x.DocumentId == document.Id);
            var request_Id_Count = request_Id.Count();
            var count = 0;
            var firstReqId = "";
            foreach (var i in request_Id)
            {
                count++;
                if (count == 1)
                {
                    firstReqId = i.Number;
                }
            }
            ViewBag.firstReqId = firstReqId;

            using (Image<Rgba32> img = new Image<Rgba32>(260, 180))
            {
                img.Mutate(x => x.Fill(Rgba32.White));
                _electronicStamp.Process(img, document.EntryNumber.ToString(), document.Date, true);
                using (var ms = new MemoryStream())
                {
                    img.SaveAsJpeg(ms);
                    string[] strArr = null;
                    string split = document.VisaId;
                    char[] splitchar = { ',' };
                    string str = "";
                    if (String.IsNullOrEmpty(split)) {// strArr[0] = VisaID;
                    } else { strArr = split.Split(splitchar); str = strArr[0]; }

                    var viewModel = new PrintViewModel
                    {
                        AppointmentNumber = document.AppointmentNumber,
                        Owner = document.Owner.Name,
                        Applicant = document.Applicant.Name,
                        Purpose = document.Purpose.Name,
                        Recipient = document.Recipient.Name,
                        VisaType = document.VisaType?.Name,
                        VisaId = str,
                        VisaDateType = document.VisaDateType?.Name,
                        VisaDate = document.VisaDate,
                        Base64Stamp = Convert.ToBase64String(ms.ToArray())
                    };

                    return View(viewModel);
                }
            }
        }

        private void SetSelectedDropDownLists(IDocumentModel document)
        {
            var selectedPurpose = _context.Purposes
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == document.PurposeId);

            var selectedStatus = _context.Statuses
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == document.StatusId);

            var selectedVisaType = _context.VisaType
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == document.VisaTypeId);

            var selectedVisaDateType = _context.VisaDateType
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == document.VisaDateTypeId);

            var selectedOwner = _context.DocumentOwners
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == document.OwnerId);

            var selectedApplicant = _context.Applicants
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == document.ApplicantId);

            PopulateApplicantsDropDownList(selectedApplicant);
            PopulateOwnersDropDownList(selectedOwner);
            PopulatePurposesDropDownList(selectedPurpose);
            PopulateStatusesDropDownList(selectedStatus);
            PopulateVisaTypeDropDownList(selectedVisaType);
            PopulateVisaDateTypeDropDownList(selectedVisaDateType);
        }

        private async Task CreateApplicantIfNotExist(DocumentViewModel viewModel)
        {
            if (viewModel.ApplicantType == ApplicantType.New)
            {
                var applicantExist = await _context.Applicants
                    .FirstOrDefaultAsync(x =>
                        string.Equals(x.Name, viewModel.ApplicantName, StringComparison.InvariantCultureIgnoreCase));

                if (applicantExist == null)
                {
                    var applicant = new Applicant {Name = viewModel.ApplicantName, Id = Guid.NewGuid()};
                    await _context.Applicants.AddAsync(applicant);
                    await _context.SaveChangesAsync();
                    viewModel.ApplicantId = applicant.Id;
                }
                else
                {
                    viewModel.ApplicantId = applicantExist.Id;
                }
            }
        }
        private async Task CreatePurposeIfNotExist(DocumentViewModel viewModel)
        {
            if (viewModel.PurposeType == PurposeType.New)
            {
                var purposesExist = await _context.Purposes
                    .FirstOrDefaultAsync(x =>
                        string.Equals(x.Name, viewModel.PurposeName, StringComparison.InvariantCultureIgnoreCase));

                if (purposesExist == null)
                {
                    var purposes = new Purpose { Name = viewModel.PurposeName, Id = Guid.NewGuid(), Character = null };
                    await _context.Purposes.AddAsync(purposes);
                    await _context.SaveChangesAsync();
                    viewModel.PurposeId = purposes.Id;
                }
                else
                {
                    viewModel.PurposeId = purposesExist.Id;
                }
            }
        }

        private void PopulatePurposesDropDownList(object selectedPurpose = null)
        {
            ViewBag.Purposes = new SelectList(_context.Purposes.AsNoTracking(),
                "Id",
                "Name",
                selectedPurpose);
        }

        private void PopulateStatusesDropDownList(object selectedStatus = null)
        {
            ViewBag.Statuses = new SelectList(_context.Statuses.AsNoTracking(),
                "Id",
                "Name",
                selectedStatus);
        }
        private void PopulateVisaTypeDropDownList(object selectedVisaType = null)
        {
            ViewBag.VisaType = new SelectList(_context.VisaType.AsNoTracking(),
                "Id",
                "Name",
                selectedVisaType);
        }

        private void PopulateVisaDateTypeDropDownList(object selectedVisaDateType = null)
        {
            ViewBag.VisaDateType = new SelectList(_context.VisaDateType.AsNoTracking(),
                "Id",
                "Name",
                selectedVisaDateType);
        }

        private void PopulateOwnersDropDownList(object selectedOwner = null)
        {
            ViewBag.Owners = new SelectList(_context.DocumentOwners.AsNoTracking(),
                "Id",
                "Name",
                selectedOwner);
        }

        private void PopulateApplicantsDropDownList(object selectedApplicant = null)
        {
            ViewBag.Applicants = new SelectList(_context.Applicants.AsNoTracking(),
                "Id",
                "Name",
                selectedApplicant);
        }
    }
}