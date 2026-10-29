---
title: Recipes
description: Short answers to common PDF tasks in C# and .NET with PdfNetStack, each with a complete program that CI compiles and runs.
sidebar:
  label: All recipes
  order: 0
---

One task per page: a short answer and a complete program you can paste and run. Every sample is
compiled and run against the library on each build of this site. The guides cover each area in
depth.

## Pages and documents

| Recipe | In one line |
|---|---|
| [Merge PDF files](/recipes/merge-pdf-files/) | Combine several PDFs into one with `PdfDocument.Merge` |
| [Split a PDF into one file per page](/recipes/split-pdf/) | One document per page with `Split` |
| [Extract pages from a PDF](/recipes/extract-pdf-pages/) | Copy chosen pages into a new file, in any order |
| [Rotate PDF pages](/recipes/rotate-pdf-pages/) | Turn pages by a multiple of 90 degrees |
| [Add page numbers](/recipes/add-page-numbers/) | Draw "Page 1 of 3" at the foot of every page |
| [Add a watermark](/recipes/add-watermark/) | A rotated, semi transparent word across every page |

## Size and quality

| Recipe | In one line |
|---|---|
| [Reduce PDF size for email](/recipes/reduce-pdf-size/) | Downsample oversized images and store photographs as JPEG |
| [Downsample images in a PDF](/recipes/downsample-pdf-images/) | Bring every image down to the resolution it is shown at |
| [Linearize a PDF for fast web view](/recipes/linearize-pdf/) | Show the first page before the whole file has downloaded |
| [Batch compress PDFs from the command line](/recipes/batch-compress-pdf-cli/) | A whole folder with `pdfnetstack batch` and a CSV report |
| [Check two PDFs for equivalence](/recipes/check-pdf-equivalence/) | Prove another tool's output holds the same document |

## Security, forms and archiving

| Recipe | In one line |
|---|---|
| [Password protect a PDF](/recipes/password-protect-pdf/) | AES-256 with user and owner passwords and permissions |
| [Remove a password from a PDF](/recipes/remove-pdf-password/) | Open with the owner password and save unprotected |
| [Fill and flatten a PDF form](/recipes/fill-and-flatten-pdf-form/) | Set field values, then stamp them into the page |
| [Flatten annotations](/recipes/flatten-pdf-annotations/) | Make comments, highlights and stamps part of the page |
| [Add a signature field](/recipes/add-signature-field/) | An empty box for a signer to sign in their viewer |
| [Create a Factur-X invoice](/recipes/pdf-a-3-xml-attachment/) | A PDF/A-3 e-invoice with its XML and Factur-X metadata |

## Content

| Recipe | In one line |
|---|---|
| [Convert an HTML invoice to PDF](/recipes/html-invoice-to-pdf/) | HTML and CSS to PDF with page numbers, no browser |
| [Convert a TIFF to PDF](/recipes/tiff-to-pdf/) | Every page of a multi-page TIFF on its own PDF page |
| [Write Arabic text in a PDF](/recipes/arabic-text-pdf/) | Right to left text with joined letters, searchable |
| [Extract all images from a PDF](/recipes/extract-images-from-pdf/) | JPEGs as stored, other images as PNG |
| [Make a scanned PDF searchable](/recipes/make-scanned-pdf-searchable/) | OCR with the `PdfNetStack.Ocr` add-on |

Recipes marked 1.1 use features that are in the main branch and not on NuGet yet. See
[Getting started](/getting-started/#build-from-source) to build against the source.
