"""Auto-generated MCP client -- do not edit by hand.

Service: bluebird -- Engineering Copilot Mini (code search)

Regenerate with:  python generate_clients.py bluebird

Show this catalog:  python -m generated.bluebird
"""
from ._runtime import BridgeClient


class BluebirdClient(BridgeClient):
    """Tools exposed by the "bluebird" MCP backend.

    - code_history: Explore code change history. Use 'diff' to see what changed in a
    commit (metadata + line-level code ...
    - code_search: Search code across the repository. Use method='keyword' when you know
    the exact identifier, class na...
    - code_navigate: Query code relationships for a code element by its name or ID.
    Always use after locating a node -- b...
    - code_read: Retrieve content from the repository. Use 'content' to read source code
    -- by file path (with option...
    - project_search: Search project-level artifacts. Use 'wiki' to search wiki pages
    for architecture docs, design specs,...
    - metadata: Server and environment introspection. Call `connection_info` first when
    you're not sure what scope (...
    """

    SERVICE = 'bluebird'

    def code_history(self, method: str, organization: str, original_user_question: str, author: str | None = None, branch: str | None = None, commit_id: str | None = None, end_time: str | None = None, file_path: str | None = None, limit: int | None = None, project: str | None = None, pull_request_id: int | None = None, query: str | None = None, repository: str | None = None, start_time: str | None = None, **extra) -> dict:
        """
        Explore code change history. Use 'diff' to see what changed in a commit
        (metadata + line-level code diffs). Use 'file_history' to trace how a file
        evolved over time. Use 'commits' to find commits by keyword, author, time range,
        or PR. Use 'pull_request' for PR details with reviewers, work items, and commit
        list.

        Args:
            method (str, required): The operation to perform. 'commits': Find commits by
            keyword (commit messages + PR titles), author, time range, or PR. For
            details on a specific commit, use 'diff' instead. 'diff': What changed in a
            specific commit - metadata (author, message, date) + file-level changes +
            line-level code diffs. Pass the full 40-character commit SHA in 'commit_id'.
            'file_history': Trace how a specific file evolved over time - all changes
            across its commit history with code diffs. 'pull_request': PR details
            including title, description, reviewers with vote status, linked work items,
            and the full commit list. Pass the numeric PR ID in 'pull_request_id'. One
            of: commits, diff, file_history, pull_request.
            organization (str, required): Azure DevOps organization name. A single
            configured scope is applied automatically (omit to use it); pass
            organization to target a different one, or to pick a scope when several are
            configured or none is. Add project to narrow to a project, and repository
            for one repo. Call `metadata` method=connection_info to see the configured
            scope(s).
            original_user_question (str, required): The user's original question or
            prompt. Pass it exactly as provided by the user.
            author (str, optional): Filter commits by author name or email. For
            'commits' filter-based search.
            branch (str, optional): Git branch to scope to (e.g. 'main', 'releases/M1').
            When set, you must also pass organization, project, and repository. Defaults
            to the repository's default branch when omitted.
            commit_id (str, optional): Full 40-character commit SHA. Required for 'diff'
            method.
            end_time (str, optional): ISO-8601 end time (e.g., 2025-12-31). For
            'commits' filter-based search.
            file_path (str, optional): File path. Required for 'file_history'.
            limit (int, optional): Maximum number of results to return.
            project (str, optional): Azure DevOps project name. Pass with organization
            to scope to a project; add repository for a single repo. Required when you
            pass a repository.
            pull_request_id (int, optional): Pull request ID number. For 'commits' to
            find commits in a PR, or for 'pull_request' to get PR details.
            query (str, optional): Search text for 'commits'. Searches commit messages
            and PR titles. Use 1-2 specific keywords -- NOT natural language sentences.
            Example: 'retry timeout' not 'fix the retry logic when requests time out'.
            repository (str, optional): Azure DevOps repository name. Pass with
            organization and project to scope to one repository. Omit it (keeping
            organization + project) to search project-wide.
            start_time (str, optional): ISO-8601 start time (e.g., 2025-01-01). For
            'commits' filter-based search.

        Conditionally required:
            passing 'project' also requires: organization
            passing 'repository' also requires: organization, project
            passing 'branch' also requires: organization, project, repository

        Returns the MCP result dict. A failed tool call still arrives as HTTP 200 --
        check result['isError'], but note some backends report failures in the text with
        isError unset, so read the content too.
        """
        arguments = {}
        arguments['method'] = method
        arguments['organization'] = organization
        arguments['original_user_question'] = original_user_question
        if author is not None:
            arguments['author'] = author
        if branch is not None:
            arguments['branch'] = branch
        if commit_id is not None:
            arguments['commit_id'] = commit_id
        if end_time is not None:
            arguments['end_time'] = end_time
        if file_path is not None:
            arguments['file_path'] = file_path
        if limit is not None:
            arguments['limit'] = limit
        if project is not None:
            arguments['project'] = project
        if pull_request_id is not None:
            arguments['pull_request_id'] = pull_request_id
        if query is not None:
            arguments['query'] = query
        if repository is not None:
            arguments['repository'] = repository
        if start_time is not None:
            arguments['start_time'] = start_time
        arguments.update(extra)
        return self._call('code_history', arguments)

    def code_search(self, method: str, organization: str, original_user_question: str, query: str, branch: str | None = None, ext: str | None = None, file: str | None = None, limit: int | None = None, path: str | None = None, project: str | None = None, repository: str | None = None, search_index: str | None = None, **extra) -> dict:
        """
        Search code across the repository. Use method='keyword' when you know the exact
        identifier, class name, or symbol. Use method='semantic' when exploring or
        discovering code by concept -- it finds code by what it does, not exact names.
        Start with semantic for open-ended questions, switch to keyword when you have
        specific names. Semantic is single-repository at the backend -- pass
        `repository` if scope doesn't pin one; method='keyword' is the broad cross-repo
        option.

        Args:
            method (str, required): The operation to perform. 'keyword': Lexical keyword
            search across all files and languages. Case-insensitive, whole-word matching
            only -- partial identifiers won't match without wildcards. Wildcards: *
            (zero or more non-whitespace chars), ? (exactly one). Short wildcard terms
            (fewer than ~5 non-wildcard chars) may miss matches due to expansion limits.
            Whole-token matching: use the exact identifier, or a trailing wildcard
            (term*) for a prefix/family -- a fast prefix lookup. When the exact name is
            unknown, use method=semantic instead. Quoted terms: "exact phrase" --
            required for multi-word terms and to match special characters ( ) [ ] : * ?.
            Escape internal quotes with backslash: "Print(\\"Hello\\")". Boolean: AND
            (default between adjacent terms), OR, NOT -- must be UPPERCASE. Parentheses
            required when mixing operators beyond AND. AND means both terms appear in
            the same file, not necessarily near each other. Proximity: BEFORE, AFTER,
            NEAR -- matches within 5 tokens, only between unprefixed terms. Example:
            field:logger AND (ILogger BEFORE logger). Code element prefixes (C#, C, C++,
            Java, VB.NET only -- zero results for other languages): Definitions:
            class:Name, method:Name, def:Name (any definition), enum:Name,
            interface:Name, field:Name, prop:Name, macro:Name (C/C++), namespace:Name.
            References: ref:Name (all usages except definition site). Content:
            comment:word, strlit:word. Inheritance: basetype:Name. Prefix means 'a
            <type> named <term>' -- class:public finds classes named 'public', not
            public classes. Use the structured ext, path, and file parameters for
            filtering -- NOT query-level ext:/path: operators. Results are node-shaped
            File candidates (path-based node_name, node_blocktype=File) usable directly
            with code_navigate (e.g. HAS_CHILD to list a file's classes/functions) and
            code_read. 'semantic': Semantic search using embeddings -- finds code by
            concept/functionality, not exact names. Best results with HyDE: generate a
            hypothetical code snippet matching your intent as the query. In hybrid mode,
            use search_index to narrow scope (e.g., 'Function' for methods, 'Class' for
            types) -- ignored in mini mode. Single-repository at the backend in both
            modes; pass `repository` if scope doesn't pin one. Returns node IDs usable
            with code_navigate and code_read (hybrid mode). Do NOT use for exact
            identifier lookup -- use keyword mode instead. One of: keyword, semantic.
            organization (str, required): Azure DevOps organization name. A single
            configured scope is applied automatically (omit to use it); pass
            organization to target a different one, or to pick a scope when several are
            configured or none is. Add project to narrow to a project, and repository
            for one repo. Call `metadata` method=connection_info to see the configured
            scope(s).
            original_user_question (str, required): The user's original question or
            prompt. Pass it exactly as provided by the user.
            query (str, required): Search text. For keyword: use code identifiers only
            -- NOT natural language. Multiple words are ANDed. Use 1-2 specific terms.
            Supports OR, NOT, "exact phrase", wildcards (*/? -- use a trailing wildcard
            like term* for a prefix; for an unknown name use method=semantic), proximity
            (BEFORE/AFTER/NEAR), and code element prefixes (class:, method:, def:, ref:,
            enum:, interface:, field:, prop:, macro:, namespace:, basetype:, comment:,
            strlit: -- C#/C/C++/Java/VB.NET only). For semantic: write a detailed
            functional description of what the code does, or generate hypothetical code
            (HyDE) that would match what you're looking for. Do NOT use single keywords
            for semantic -- describe behavior, purpose, or structure.
            branch (str, optional): Git branch to scope to (e.g. 'main', 'releases/M1').
            When set, you must also pass organization, project, and repository. Defaults
            to the repository's default branch when omitted.
            ext (str, optional): Filter by file extension without the dot (e.g., 'cs',
            'tsx', 'sql'). For keyword mode only. Do not include periods -- if present,
            everything up to the last period is stripped (e.g., ext:'Tests.proj' is
            treated as ext:'proj', matching all .proj files).
            file (str, optional): Filter by file name pattern (e.g., 'queueRegister*').
            Supports wildcards. For keyword mode only.
            limit (int, optional): Maximum number of results to return.
            path (str, optional): Filter by directory path from repository root (e.g.,
            '/src/services'). For keyword mode only.
            project (str, optional): Azure DevOps project name. Pass with organization
            to scope to a project; add repository for a single repo. Required when you
            pass a repository.
            repository (str, optional): Azure DevOps repository name. Pass with
            organization and project to scope to one repository. Omit it (keeping
            organization + project) to search project-wide.
            search_index (str, optional): For semantic mode: which index to search. Use
            'General' when unsure. One of: General, File, Class, Struct, Function,
            Macro.

        Conditionally required:
            passing 'project' also requires: organization
            passing 'repository' also requires: organization, project
            passing 'branch' also requires: organization, project, repository

        Returns the MCP result dict. A failed tool call still arrives as HTTP 200 --
        check result['isError'], but note some backends report failures in the text with
        isError unset, so read the content too.
        """
        arguments = {}
        arguments['method'] = method
        arguments['organization'] = organization
        arguments['original_user_question'] = original_user_question
        arguments['query'] = query
        if branch is not None:
            arguments['branch'] = branch
        if ext is not None:
            arguments['ext'] = ext
        if file is not None:
            arguments['file'] = file
        if limit is not None:
            arguments['limit'] = limit
        if path is not None:
            arguments['path'] = path
        if project is not None:
            arguments['project'] = project
        if repository is not None:
            arguments['repository'] = repository
        if search_index is not None:
            arguments['search_index'] = search_index
        arguments.update(extra)
        return self._call('code_search', arguments)

    def code_navigate(self, method: str, organization: str, original_user_question: str, branch: str | None = None, connected_node_types: str | None = None, node_id: int | None = None, node_name: str | None = None, node_type: str | None = None, project: str | None = None, repository: str | None = None, view: str | None = None, **extra) -> dict:
        """
        Query code relationships for a code element by its name or ID. Always use after
        locating a node -- behavioral relationships (CALLS, CALLED_BY, ACCESSES,
        ACCESSED_BY) reveal how code actually works, beyond just its structure. The
        'method' parameter must be one of the UPPERCASE relationship types listed in the
        enum. Call graph: CALLS, CALLED_BY. Inheritance: DERIVES_FROM (parent types),
        BASE_TYPE_OF (child types), OVERRIDES/OVERRIDDEN_BY. Interfaces:
        IMPLEMENTS/IMPLEMENTED_BY. Members: MEMBER_FUNCTIONS (methods -- results include
        returnType), MEMBER_VARIABLES (fields -- results include type name), HAS_CHILD
        (all children), NESTED_TYPES. Containment: MEMBER_OF (direct parent class/file),
        CONTAINED_BY (all ancestors). Data access: ACCESSES/ACCESSED_BY. Macros:
        EXPANDS/EXPANDED_BY. Types: TYPE_OF/TYPE_FOR, DECLARES/DECLARED_BY. Other:
        INSTANTIATES/INSTANTIATED_BY, IMPORTED_BY, QUALIFIED_BY, EXTENDED_BY (partial
        class across files -- NOT inheritance), CONSTRAINED_AS. Common patterns -- for a
        class: MUST call DERIVES_FROM, BASE_TYPE_OF, HAS_CHILD, MEMBER_OF (all four
        required even if you read the source), then MEMBER_FUNCTIONS + MEMBER_VARIABLES
        for listings with types, then CALLS/ACCESSES on key members for behavior. For a
        function: CALLS, CALLED_BY, ACCESSES, MEMBER_OF. MEMBER_FUNCTIONS vs HAS_CHILD:
        MEMBER_FUNCTIONS returns only methods with returnType; HAS_CHILD returns
        everything -- use connected_node_types to filter. High-cardinality reverse
        relationships capped at 200 entries -- '__truncated__' sentinel appended when
        hit. Re-query with node_id for specific elements, or use keyword search for
        exhaustive results.

        Args:
            method (str, required): The operation to perform. 'CALLS': What functions
            does X call? Use to trace outgoing call chains. 'CALLED_BY': What calls X?
            'OVERRIDES': Methods that X overrides (derived → base). 'OVERRIDDEN_BY':
            Methods that override X (base → derived). 'DERIVES_FROM': Parent/base types
            of X. 'BASE_TYPE_OF': Child/derived types of X. 'IMPLEMENTS': Interfaces
            that X implements. 'IMPLEMENTED_BY': Types that implement interface X.
            'INSTANTIATES': What types does X create? 'INSTANTIATED_BY': What creates X?
            'ACCESSES': What variables does X read/write? Use to trace data access
            patterns. 'ACCESSED_BY': What reads/writes X? 'EXPANDS': What macros does X
            expand? 'EXPANDED_BY': What expands macro X? 'DECLARES': What does X
            declare? 'DECLARED_BY': What declares X? 'TYPE_OF': Type of a
            variable/field. 'MEMBER_OF': Parent class/struct/file of a function or
            variable. Use connected_node_types='Class,Struct' to exclude File results.
            'MEMBER_FUNCTIONS': All methods of a class/struct. Results include
            returnType field. Use instead of HAS_CHILD when you only need methods.
            'MEMBER_VARIABLES': All fields of a class/struct. Results include type name
            field. 'NESTED_TYPES': Classes/structs nested inside X. 'HAS_CHILD': All
            direct children (functions, variables, nested types). Use
            connected_node_types to filter by element type. 'CONTAINED_BY': All ancestor
            nodes at any nesting depth. 'TYPE_FOR': What variables/fields have this
            type? 'IMPORTED_BY': What imports this file? 'QUALIFIED_BY': Qualifying type
            of a node. 'EXTENDED_BY': Partial class/struct extensions across files (NOT
            inheritance). 'CONSTRAINED_AS': Generic type constraint. One of: CALLS,
            CALLED_BY, OVERRIDES, OVERRIDDEN_BY, DERIVES_FROM, BASE_TYPE_OF, IMPLEMENTS,
            IMPLEMENTED_BY, INSTANTIATES, INSTANTIATED_BY, ACCESSES, ACCESSED_BY,
            EXPANDS, EXPANDED_BY, DECLARES, DECLARED_BY, TYPE_OF, MEMBER_OF,
            MEMBER_FUNCTIONS, MEMBER_VARIABLES, NESTED_TYPES, HAS_CHILD, CONTAINED_BY,
            TYPE_FOR, IMPORTED_BY, QUALIFIED_BY, EXTENDED_BY, CONSTRAINED_AS.
            organization (str, required): Azure DevOps organization name. A single
            configured scope is applied automatically (omit to use it); pass
            organization to target a different one, or to pick a scope when several are
            configured or none is. Add project to narrow to a project, and repository
            for one repo. Call `metadata` method=connection_info to see the configured
            scope(s).
            original_user_question (str, required): The user's original question or
            prompt. Pass it exactly as provided by the user.
            branch (str, optional): Git branch to scope to (e.g. 'main', 'releases/M1').
            When set, you must also pass organization, project, and repository. Defaults
            to the repository's default branch when omitted.
            connected_node_types (str, optional): Comma-separated filter for connected
            node types. E.g. 'Class,Struct' with HAS_CHILD to get only nested types, or
            'Class,Struct' with MEMBER_OF to exclude File results and get only parent
            classes. Default '' returns all types (no filter).
            node_id (int, optional): Numeric node ID from a previous query. ALWAYS pass
            when available -- it is the only reliable way to resolve ambiguous or
            overloaded names.
            node_name (str, optional): The simple name of the code element (not
            qualified 'Class::Method'). Examples: 'GenericEvent',
            'ValidateReserveSpace'. Required unless node_id is provided.
            node_type (str, optional): The type of code element. One of: Function,
            Class, Struct, Interface, Macro, Field, Variable, Typedef, Enum, File,
            FunctionDeclaration.
            project (str, optional): Azure DevOps project name. Pass with organization
            to scope to a project; add repository for a single repo. Required when you
            pass a repository.
            repository (str, optional): Azure DevOps repository name. Pass with
            organization and project to scope to one repository. Omit it (keeping
            organization + project) to search project-wide.
            view (str, optional): Only affects 2-hop methods (CALLS, CALLED_BY, EXPANDS,
            EXPANDED_BY, INSTANTIATES, INSTANTIATED_BY, ACCESSES, ACCESSED_BY).
            'definition' (default) -- returns the related code element with full source.
            Use to understand WHAT the related code is. 'call_site' -- returns the
            intermediate node (Invocation/Statement) with exact file/line. Use to find
            WHERE the relationship occurs in source. One of: definition, call_site.

        Conditionally required:
            passing 'project' also requires: organization
            passing 'repository' also requires: organization, project
            passing 'branch' also requires: organization, project, repository

        Returns the MCP result dict. A failed tool call still arrives as HTTP 200 --
        check result['isError'], but note some backends report failures in the text with
        isError unset, so read the content too.
        """
        arguments = {}
        arguments['method'] = method
        arguments['organization'] = organization
        arguments['original_user_question'] = original_user_question
        if branch is not None:
            arguments['branch'] = branch
        if connected_node_types is not None:
            arguments['connected_node_types'] = connected_node_types
        if node_id is not None:
            arguments['node_id'] = node_id
        if node_name is not None:
            arguments['node_name'] = node_name
        if node_type is not None:
            arguments['node_type'] = node_type
        if project is not None:
            arguments['project'] = project
        if repository is not None:
            arguments['repository'] = repository
        if view is not None:
            arguments['view'] = view
        arguments.update(extra)
        return self._call('code_navigate', arguments)

    def code_read(self, method: str, organization: str, original_user_question: str, branch: str | None = None, end_line: int | None = None, limit: int | None = None, node_id: int | None = None, node_name: str | None = None, node_type: str | None = None, path: str | None = None, project: str | None = None, query: str | None = None, repository: str | None = None, start_line: int | None = None, **extra) -> dict:
        """
        Retrieve content from the repository. Use 'content' to read source code -- by
        file path (with optional line range) or by graph node name/ID. Use 'summary' to
        get a structured hierarchical summary of a code element. Use 'directory' to list
        files and subdirectories at a path. Use 'files' to find files by name or path
        pattern.

        Args:
            method (str, required): The operation to perform. 'content': Read source
            code. A file (by 'path', or a File-typed/path-shaped 'node_name') is read
            live from the repository; a code element (simple 'node_name'/'node_id' from
            semantic search, hybrid only) returns the element's source. Optional line
            range via 'start_line'/'end_line'. 'directory': List files and
            subdirectories at a given path. Use to explore project structure. Defaults
            to root directory if path is omitted. 'files': Find files by name or path
            pattern. Substring matching is automatic. Supports wildcards (* and ?) for
            advanced patterns. One of: content, directory, files.
            organization (str, required): Azure DevOps organization name. A single
            configured scope is applied automatically (omit to use it); pass
            organization to target a different one, or to pick a scope when several are
            configured or none is. Add project to narrow to a project, and repository
            for one repo. Call `metadata` method=connection_info to see the configured
            scope(s).
            original_user_question (str, required): The user's original question or
            prompt. Pass it exactly as provided by the user.
            branch (str, optional): Git branch to scope to (e.g. 'main', 'releases/M1').
            When set, you must also pass organization, project, and repository. Defaults
            to the repository's default branch when omitted.
            end_line (int, optional): Last line number to retrieve (1-based, inclusive).
            For 'content' with a file path.
            limit (int, optional): Maximum number of results. For 'files' method.
            node_id (int, optional): Numeric node ID from a previous query. For
            'content' method -- hybrid only. ALWAYS pass when available for precise
            resolution.
            node_name (str, optional): Graph node name to read source code for (e.g.,
            'GenericEvent', 'ValidateReserveSpace'). Use simple names only -- not
            qualified ('Class::Method'). For 'content' and 'summary' methods -- hybrid
            only. The graph resolves the name to file + line range.
            node_type (str, optional): Type of code element for node-based lookup. For
            'content' and 'summary' -- hybrid only. One of: File, Class, Function,
            Struct, Macro, Field, Enum, Union, Variable, Typedef.
            path (str, optional): File or directory path from repository root (e.g.,
            '/src/auth.cs'). Do not abbreviate or shorten paths -- use the full path
            from search results. Required for 'content' (when reading by path). Optional
            for 'directory' (defaults to '/') and 'files'.
            project (str, optional): Azure DevOps project name. Pass with organization
            to scope to a project; add repository for a single repo. Required when you
            pass a repository.
            query (str, optional): File name or path pattern to search for. For 'files'
            method. Substring matching is automatic. Supports wildcards (* and ?).
            repository (str, optional): Azure DevOps repository name. Pass with
            organization and project to scope to one repository. Omit it (keeping
            organization + project) to search project-wide.
            start_line (int, optional): First line number to retrieve (1-based,
            inclusive). For 'content' with a file path.

        Conditionally required:
            passing 'project' also requires: organization
            passing 'repository' also requires: organization, project
            passing 'branch' also requires: organization, project, repository

        Returns the MCP result dict. A failed tool call still arrives as HTTP 200 --
        check result['isError'], but note some backends report failures in the text with
        isError unset, so read the content too.
        """
        arguments = {}
        arguments['method'] = method
        arguments['organization'] = organization
        arguments['original_user_question'] = original_user_question
        if branch is not None:
            arguments['branch'] = branch
        if end_line is not None:
            arguments['end_line'] = end_line
        if limit is not None:
            arguments['limit'] = limit
        if node_id is not None:
            arguments['node_id'] = node_id
        if node_name is not None:
            arguments['node_name'] = node_name
        if node_type is not None:
            arguments['node_type'] = node_type
        if path is not None:
            arguments['path'] = path
        if project is not None:
            arguments['project'] = project
        if query is not None:
            arguments['query'] = query
        if repository is not None:
            arguments['repository'] = repository
        if start_line is not None:
            arguments['start_line'] = start_line
        arguments.update(extra)
        return self._call('code_read', arguments)

    def project_search(self, method: str, organization: str, original_user_question: str, query: str, limit: int | None = None, project: str | None = None, wiki: str | None = None, **extra) -> dict:
        """
        Search project-level artifacts. Use 'wiki' to search wiki pages for architecture
        docs, design specs, SOPs, and rollout plans. Use 'work_items' to search bugs,
        user stories, tasks, and features with inline filters.

        Args:
            method (str, required): The operation to perform. 'wiki': Search wiki pages
            for architecture docs, design specs, SOPs, rollout plans, and feature
            documentation. 'work_items': Search bugs, user stories, tasks, and features.
            Supports inline filters: t: (type), s: (state), a: (assigned to), c:
            (created by), tags:, area:, iteration:. One of: wiki, work_items.
            organization (str, required): Azure DevOps organization name. A single
            configured scope is applied automatically (omit to use it); pass
            organization to target a different one, or to pick a scope when several are
            configured or none is. Add project to narrow to a project, and repository
            for one repo. Call `metadata` method=connection_info to see the configured
            scope(s).
            original_user_question (str, required): The user's original question or
            prompt. Pass it exactly as provided by the user.
            query (str, required): Search keywords. For 'wiki': plain text, multiple
            words are ANDed. For 'work_items': supports inline filters -- t: (type), s:
            (state), a: (assigned to), c: (created by), tags:, area:, iteration:. Date
            operators: CreatedDate>@Today-7. Examples: 'authentication t:Bug s:Active',
            'a:John tags:Critical'.
            limit (int, optional): Maximum number of results to return.
            project (str, optional): Azure DevOps project name. Pass with organization
            to scope to a project; add repository for a single repo. Required when you
            pass a repository.
            wiki (str, optional): Wiki name to scope search to a specific wiki. For
            'wiki' method. Omit to search all wikis in the project.

        Conditionally required:
            passing 'project' also requires: organization

        Returns the MCP result dict. A failed tool call still arrives as HTTP 200 --
        check result['isError'], but note some backends report failures in the text with
        isError unset, so read the content too.
        """
        arguments = {}
        arguments['method'] = method
        arguments['organization'] = organization
        arguments['original_user_question'] = original_user_question
        arguments['query'] = query
        if limit is not None:
            arguments['limit'] = limit
        if project is not None:
            arguments['project'] = project
        if wiki is not None:
            arguments['wiki'] = wiki
        arguments.update(extra)
        return self._call('project_search', arguments)

    def metadata(self, method: str, original_user_question: str, organization: str | None = None, project: str | None = None, repository: str | None = None, **extra) -> dict:
        """
        Server and environment introspection. Call `connection_info` first when you're
        not sure what scope (org/project/repo/branch) the server is configured with --
        the other tools silently fall back to that configured scope when their
        parameters are omitted. Use `list_orgs` to discover which Azure DevOps
        organizations you have access to. Use `repo_status` to check a repository's
        indexing mode and branch heads.

        Args:
            method (str, required): The operation to perform. 'list_orgs': List Azure
            DevOps organizations the authenticated user has access to. Use this to
            bootstrap when no organization is configured. 'connection_info': Lists the
            connection's configured scope(s) -- organization, project, repository,
            branch -- and each one's resolved mode (hybrid graph/vector vs mini
            keyword/file). Call this first to discover what scopes the MCP connection
            was configured for; on a multi-scope connection it lists them all so you can
            issue one call per scope. The org/project/repository params on other tools
            are optional and silently fall back to a single configured scope.
            'repo_status': Returns the indexing mode and indexed branches for a
            repository. Useful for confirming whether a repo supports hybrid search and
            which branches are searchable. One of: list_orgs, connection_info,
            repo_status.
            original_user_question (str, required): The user's original question or
            prompt. Pass it exactly as provided by the user.
            organization (str, optional): Azure DevOps organization name. A single
            configured scope is applied automatically (omit to use it); pass
            organization to target a different one, or to pick a scope when several are
            configured or none is. Add project to narrow to a project, and repository
            for one repo. Call `metadata` method=connection_info to see the configured
            scope(s).
            project (str, optional): Azure DevOps project name. Pass with organization
            to scope to a project; add repository for a single repo. Required when you
            pass a repository.
            repository (str, optional): Azure DevOps repository name. Pass with
            organization and project to scope to one repository. Omit it (keeping
            organization + project) to search project-wide.

        Conditionally required:
            passing 'project' also requires: organization
            passing 'repository' also requires: organization, project

        Returns the MCP result dict. A failed tool call still arrives as HTTP 200 --
        check result['isError'], but note some backends report failures in the text with
        isError unset, so read the content too.
        """
        arguments = {}
        arguments['method'] = method
        arguments['original_user_question'] = original_user_question
        if organization is not None:
            arguments['organization'] = organization
        if project is not None:
            arguments['project'] = project
        if repository is not None:
            arguments['repository'] = repository
        arguments.update(extra)
        return self._call('metadata', arguments)


if __name__ == "__main__":
    BluebirdClient.help()
