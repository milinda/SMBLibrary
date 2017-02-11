/* Copyright (C) 2017 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using SMBLibrary.Authentication;
using SMBLibrary.SMB2;
using Utilities;

namespace SMBLibrary.Server.SMB2
{
    public class QueryDirectoryHelper
    {
        internal static SMB2Command GetQueryDirectoryResponse(QueryDirectoryRequest request, ISMBShare share, SMB2ConnectionState state)
        {
            SMB2Session session = state.GetSession(request.Header.SessionID);
            OpenFileObject openFile = session.GetOpenFileObject(request.FileId.Persistent);
            if (openFile == null)
            {
                return new ErrorResponse(request.CommandName, NTStatus.STATUS_FILE_CLOSED);
            }

            if (!((FileSystemShare)share).HasReadAccess(session.SecurityContext, openFile.Path))
            {
                return new ErrorResponse(request.CommandName, NTStatus.STATUS_ACCESS_DENIED);
            }

            FileSystemShare fileSystemShare = (FileSystemShare)share;

            ulong fileID = request.FileId.Persistent;
            OpenSearch openSearch = session.GetOpenSearch(fileID);
            if (openSearch == null || request.Reopen)
            {
                if (request.Reopen)
                {
                    session.RemoveOpenSearch(fileID);
                }
                List<QueryDirectoryFileInformation> entries;
                NTStatus searchStatus = share.FileStore.QueryDirectory(out entries, openFile.Handle, request.FileName, request.FileInformationClass);
                if (searchStatus != NTStatus.STATUS_SUCCESS)
                {
                    state.LogToServer(Severity.Verbose, "Query Directory: Path: '{0}', Searched for '{1}', NTStatus: {2}", openFile.Path, request.FileName, searchStatus.ToString());
                    return new ErrorResponse(request.CommandName, searchStatus);
                }
                state.LogToServer(Severity.Verbose, "Query Directory: Path: '{0}', Searched for '{1}', found {2} matching entries", openFile.Path, request.FileName, entries.Count);
                openSearch = session.AddOpenSearch(fileID, entries, 0);
            }

            if (request.Restart || request.Reopen)
            {
                openSearch.EnumerationLocation = 0;
            }

            if (openSearch.Entries.Count == 0)
            {
                // [MS-SMB2] If there are no entries to return [..] the server MUST fail the request with STATUS_NO_SUCH_FILE.
                session.RemoveOpenSearch(fileID);
                return new ErrorResponse(request.CommandName, NTStatus.STATUS_NO_SUCH_FILE);
            }

            if (openSearch.EnumerationLocation == openSearch.Entries.Count)
            {
                return new ErrorResponse(request.CommandName, NTStatus.STATUS_NO_MORE_FILES);
            }

            List<QueryDirectoryFileInformation> page = new List<QueryDirectoryFileInformation>();
            int pageLength = 0;
            for (int index = openSearch.EnumerationLocation; index < openSearch.Entries.Count; index++)
            {
                QueryDirectoryFileInformation fileInformation = openSearch.Entries[index];
                if (fileInformation.FileInformationClass != request.FileInformationClass)
                {
                    // We do not support changing FileInformationClass during a search (unless SMB2_REOPEN is set).
                    return new ErrorResponse(request.CommandName, NTStatus.STATUS_INVALID_PARAMETER);
                }

                if (pageLength + fileInformation.Length <= request.OutputBufferLength)
                {
                    page.Add(fileInformation);
                    pageLength += fileInformation.Length;
                    openSearch.EnumerationLocation = index + 1;
                }
                else
                {
                    break;
                }

                if (request.ReturnSingleEntry)
                {
                    break;
                }
            }
            
            QueryDirectoryResponse response = new QueryDirectoryResponse();
            response.SetFileInformationList(page);
            return response;
        }
    }
}
