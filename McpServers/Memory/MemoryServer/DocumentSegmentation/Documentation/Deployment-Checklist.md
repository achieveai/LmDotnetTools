# Document Segmentation - Deployment Checklist

## Pre-Deployment

- [ ] **Database Schema**: Ensure migration scripts are applied
- [ ] **Configuration**: Verify appsettings.json has DocumentSegmentation section
- [ ] **Prompts File**: Deploy prompts.yml to correct location
- [ ] **Dependencies**: Verify all NuGet packages are installed
- [ ] **LLM Provider**: Configure Anthropic/OpenAI API keys
- [ ] **SQLite FTS**: Confirm FTS5 extension is available

## Deployment Steps

- [ ] **Build Project**: `dotnet build --configuration Release`
- [ ] **Run Tests**: `dotnet test --filter "DocumentSegmentation"`
- [ ] **Deploy Binaries**: Copy to production environment
- [ ] **Update Configuration**: Environment-specific settings
- [ ] **Database Migration**: Apply schema changes
- [ ] **Service Registration**: Verify DI container registration
- [ ] **Health Checks**: Configure monitoring endpoints

## Post-Deployment Validation

- [ ] **Service Health**: Check `/health` endpoint
- [ ] **Basic Segmentation**: Test with sample document
- [ ] **Database Connectivity**: Verify segment storage
- [ ] **Search Functionality**: Test segment search
- [ ] **Session Isolation**: Verify multi-user separation
- [ ] **Quality Assessment**: Confirm quality scoring
- [ ] **Performance Metrics**: Monitor response times
- [ ] **Logging**: Verify structured logging output

## Configuration Verification

- [ ] **Thresholds**: Document size thresholds match requirements
- [ ] **Quality Scores**: Minimum quality thresholds configured
- [ ] **Cache Settings**: Appropriate cache expiration times
- [ ] **Concurrency**: Max concurrent operations set correctly
- [ ] **Timeouts**: LLM timeout values appropriate for environment
- [ ] **Retry Logic**: Max retries configured for reliability

## Monitoring Setup

- [ ] **Application Logs**: Document segmentation events logged
- [ ] **Performance Counters**: Response time tracking
- [ ] **Error Rates**: Segmentation failure monitoring
- [ ] **Database Metrics**: Query performance tracking
- [ ] **Quality Metrics**: Average quality scores
- [ ] **Usage Analytics**: Segmentation frequency and patterns

## Troubleshooting Guide

### Common Issues

1. **Segmentation Not Triggered**
   - Check document word count vs configured thresholds
   - Verify DocumentType is correctly identified
   - Review MemoryService integration logs

2. **Poor Quality Scores**
   - Validate prompt templates in prompts.yml
   - Check LLM provider connectivity
   - Review strategy selection logic

3. **Database Errors**
   - Verify SQLite file permissions
   - Check FTS5 table creation
   - Validate connection string

4. **Search Not Working**
   - Confirm FTS5 index population
   - Test fallback search mechanism
   - Check session context filtering

### Recovery Procedures

- **Database Corruption**: Restore from backup, rebuild FTS indexes
- **Prompt Loading Failures**: Verify file paths, check YAML syntax
- **Memory Leaks**: Monitor cache usage, restart service if needed
- **Performance Degradation**: Analyze query patterns, optimize indexes

## Rollback Plan

If issues occur:

1. **Stop Traffic**: Route requests away from affected instances
2. **Revert Code**: Deploy previous stable version
3. **Database Rollback**: Revert schema changes if necessary
4. **Configuration Reset**: Restore previous configuration files
5. **Verify Functionality**: Test core features before resuming traffic
6. **Post-Mortem**: Document issues and prevention strategies

---

*Deployment Date: _______________*
*Deployed By: _______________*
*Version: 1.0.0*
