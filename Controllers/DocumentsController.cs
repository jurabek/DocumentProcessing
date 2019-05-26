using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using DocumentProcessing.Abstraction;
using DocumentProcessing.Data;
using DocumentProcessing.Models;
using DocumentProcessing.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DocumentProcessing.Controllers
{
    [Authorize]
    public class DocumentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IMapper _mapper;

        public DocumentsController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IMapper mapper)
        {
            _context = context;
            _userManager = userManager;
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<IActionResult> Index([FromQuery(Name = "q")] string searchText)
        {
            var documents = _context.Documents
                .Select(d => new Document
                {
                    Id = d.Id,
                    Date = d.Date,
                    EntryNumber = d.EntryNumber,
                    AppointmentNumber = d.AppointmentNumber,
                    ApplicantId = d.ApplicantId,
                    StatusId = d.StatusId,
                    PurposeId = d.PurposeId,
                    OwnerId = d.OwnerId,
                    RecipientId = d.RecipientId,
                    Recipient = d.Recipient,
                    Applicant = d.Applicant,
                    Owner = d.Owner,
                    Purpose = d.Purpose,
                    Status = d.Status,
                    ScannedFiles = d.ScannedFiles.AsQueryable().Select(x => new ScannedFile
                    {
                        FileName = x.FileName, 
                        Id = x.Id
                    }).ToList()
                });

            if (!string.IsNullOrEmpty(searchText))
            {
                var filteredDocuments = documents
                    .Where(x => x.Applicant.Name.Contains(searchText)
                                             || x.Owner.Name.Contains(searchText)
                                             || x.EntryNumber.ToString() == searchText
                                             || x.AppointmentNumber == searchText
                                             || x.Recipient.Name.Contains(searchText)
                                             || x.Purpose.Name.Contains(searchText)
                                             || x.Status.Name.Contains(searchText)).ToList();

                var result = _mapper.Map<IEnumerable<DocumentListViewModel>>(filteredDocuments);
                ViewBag.SearchText = searchText;
                return View(result);
            }

            var list = _mapper.Map<IEnumerable<DocumentListViewModel>>(documents.ToList());
            return View(list);
        }

        [HttpGet]
        public IActionResult Create()
        {
            PopulateOwnersDropDownList(_context.DocumentOwners.FirstOrDefault());
            PopulateApplicantsDropDownList();
            PopulateStatusesDropDownList();
            PopulatePurposesDropDownList();

            return View(new DocumentViewModel());
        }

        [HttpPost]
        public async Task<IActionResult> Create(DocumentViewModel viewModel, IList<IFormFile> files)
        {
            ValidateApplicant(viewModel);

            if (ModelState.IsValid)
            {
                await CreateApplicantIfNotExist(viewModel);
                var document = _mapper.Map<Document>(viewModel);

                if (files.Any())
                {
                    document.ScannedFiles = GetScannedFiles(files);
                    ;
                }

                await _context.AddAsync(document);
                try
                {
                    await _context.SaveChangesAsync();
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateException)
                {
                    ModelState.AddModelError("", "Unable to save changes. " +
                                                 "Try again, and if the problem persists, " +
                                                 "see your system administrator.");
                }
            }

            SetSelectedDropDownLists(viewModel);
            return View(viewModel);
        }

        private static IList<ScannedFile> GetScannedFiles(IList<IFormFile> files)
        {
            IList<ScannedFile> scannedFiles = new List<ScannedFile>();
            foreach (var f in files)
            {
                using (var ms = new MemoryStream())
                {
                    f.CopyTo(ms);
                    scannedFiles.Add(new ScannedFile
                    {
                        FileName = f.FileName,
                        ContentType = f.ContentType,
                        Length = f.Length,
                        File = ms.ToArray()
                    });
                }
            }

            return scannedFiles;
        }

        public IActionResult Edit(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var document = _context.Documents
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == id);

            if (document == null)
            {
                return NotFound();
            }

            var viewModel = _mapper.Map<DocumentViewModel>(document);
            viewModel.ApplicantType = ApplicantType.Existing;
            SetSelectedDropDownLists(document);

            return View(viewModel);
        }

        [HttpPost, ActionName("Edit")]
        public async Task<IActionResult> EditPost(DocumentViewModel viewModel, IList<IFormFile> files)
        {
            ValidateApplicant(viewModel);

            if (ModelState.IsValid)
            {
                var originalDocument = _context.Documents
                    .FirstOrDefault(x => x.Id == viewModel.Id);

                if (originalDocument == null)
                {
                    return NotFound();
                }

                await CreateApplicantIfNotExist(viewModel);
                var document = _mapper.Map<Document>(viewModel);

                if (HasChangesBetweenTwoDocuments(originalDocument, document))
                {
                    try
                    {
                        document.Date = originalDocument.Date;
                        
                        _context.Update(document);
                        await _context.SaveChangesAsync();
                        return RedirectToAction(nameof(Index));
                    }
                    catch (DbUpdateException)
                    {
                        ModelState.AddModelError("", "Unable to save changes. " +
                                                     "Try again, and if the problem persists, " +
                                                     "see your system administrator.");
                    }
                }
            }

            SetSelectedDropDownLists(viewModel);
            return View(viewModel);
        }

        private bool HasChangesBetweenTwoDocuments(Document originalDocument, Document document)
        {
            return originalDocument.ApplicantId != document.ApplicantId
                   || originalDocument.AppointmentNumber != document.AppointmentNumber
                   || originalDocument.EntryNumber != document.EntryNumber
                   || originalDocument.OwnerId != document.OwnerId
                   || originalDocument.PurposeId != document.PurposeId
                   || originalDocument.StatusId != document.StatusId
                   || originalDocument.ApplicantId != document.ApplicantId;
        }

        private void ValidateApplicant(DocumentViewModel viewModel)
        {
            if (viewModel.ApplicantId == null && string.IsNullOrEmpty(viewModel.ApplicantName))
            {
                ModelState.AddModelError("", "Номи ташкилот холи аст!");
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
        }

        private async Task CreateApplicantIfNotExist(DocumentViewModel viewModel)
        {
            if (viewModel.ApplicantType == ApplicantType.New)
            {
                var applicantExist = await _context.Applicants
                    .FirstOrDefaultAsync(x => x.Name == viewModel.ApplicantName);

                if (applicantExist == null)
                {
                    var applicant = new Applicant {Name = viewModel.ApplicantName, Id = Guid.NewGuid()};
                    await _context.Applicants.AddAsync(applicant);
                    await _context.SaveChangesAsync();
                    viewModel.ApplicantId = applicant.Id;
                }

                viewModel.ApplicantId = applicantExist?.Id;
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
                .FirstOrDefault();

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
    }
}